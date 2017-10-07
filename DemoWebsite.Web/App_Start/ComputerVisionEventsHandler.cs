using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DemoWebsite.Web.Helpers;
using DemoWebsite.Web.Models;
using Microsoft.ProjectOxford.Vision;
using Microsoft.ProjectOxford.Vision.Contract;
using Newtonsoft.Json;
using Umbraco.Core;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using File = System.IO.File;


namespace DemoWebsite.Web
{

    public class ComputerVisionEventsHandler : ApplicationEventHandler
    {
        private readonly string _visionApiKey = ConfigurationManager.AppSettings["VisionApiKey"];
        private readonly string _visionApiUrl = ConfigurationManager.AppSettings["VisionApiUrl"];

        private readonly MediaFileSystem _fs = FileSystemProviderManager.Current.GetFileSystemProvider<MediaFileSystem>();

        protected override void ApplicationStarted(UmbracoApplicationBase umbracoApplication, ApplicationContext applicationContext)
        {
            MediaService.Saving += MediaService_Saving;
        }

        private void MediaService_Saving(IMediaService sender, SaveEventArgs<IMedia> e)
        {
            VisionServiceClient visionServiceClient = new VisionServiceClient(_visionApiKey, _visionApiUrl);

            foreach (IMedia media in e.SavedEntities)
            {
                string relativeImagePath = ImagePathHelper.GetImageFilePath(media);
                string fullPath = _fs.GetFullPath(relativeImagePath);

                // Computer Vision API
                using (Stream imageFileStream = File.OpenRead(fullPath))
                {
                    // Call the Computer Vision API
                    Task<AnalysisResult> analysisResultTask = visionServiceClient
                        .AnalyzeImageAsync(
                            imageFileStream, 
                            new []
                            {
                                VisualFeature.Description,
                                VisualFeature.Adult,
                                VisualFeature.Tags,
                                VisualFeature.Categories
                            },
                            new[]
                            {
                                "celebrities", "landmarks"
                            }
                        );
                    analysisResultTask.Wait();

                    var computervisionResult = analysisResultTask.Result;


                    // Get the result and set the values of the ContentItem
                    var celebrityTags = new List<string>();
                    var landmarksTags = new List<string>();

                    foreach (Category category in computervisionResult.Categories.Where(a => a.Detail!=null))
                    {
                        var detailResult = JsonConvert.DeserializeObject<DetailsModel>(category.Detail.ToString());
                        celebrityTags.AddRange(detailResult.Celebrities.Select(a => a.Name));
                        landmarksTags.AddRange(detailResult.Landmarks.Select(a => a.Name));
                    }

                    IEnumerable<string> tags = computervisionResult.Tags.Select(a => a.Name);
                    string caption = computervisionResult.Description.Captions.First().Text;
                    bool isAdult = computervisionResult.Adult.IsAdultContent;
                    bool isRacy = computervisionResult.Adult.IsRacyContent;

                    media.SetTags("tags", tags, true);
                    media.SetTags("celebrities", celebrityTags, true);
                    media.SetTags("landmarks", landmarksTags, true);
                    media.SetValue("description", caption);
                    media.SetValue("isAdult", isAdult);
                    media.SetValue("isRacy", isRacy);
                }
            }
        }
    }
}