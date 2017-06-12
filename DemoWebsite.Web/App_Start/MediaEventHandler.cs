using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Microsoft.ProjectOxford.Vision;
using Microsoft.ProjectOxford.Vision.Contract;
using Newtonsoft.Json;
using Umbraco.Core;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using Umbraco.Web.Models;
using File = System.IO.File;


namespace DemoWebsite.Web.App_Start
{

    public class MediaEventHandler : ApplicationEventHandler
    {
        protected override void ApplicationStarted(UmbracoApplicationBase umbracoApplication, ApplicationContext applicationContext)
        {
            MediaService.Saving += MediaService_Saving;
        }

        void MediaService_Saving(IMediaService sender, SaveEventArgs<Umbraco.Core.Models.IMedia> e)
        {
            string visionApiKey = ConfigurationManager.AppSettings["VisionApiKey"];
            string visionApiUrl = ConfigurationManager.AppSettings["VisionApiUrl"];

            var fs = FileSystemProviderManager.Current.GetFileSystemProvider<MediaFileSystem>();
            VisionServiceClient visionServiceClient = new VisionServiceClient(visionApiKey, visionApiUrl);

            foreach (IMedia media in e.SavedEntities)
            {
                
                string relativeImagePath = GetImageFilePath(media);
                string fullPath = fs.GetFullPath(relativeImagePath);

                using (Stream imageFileStream = File.OpenRead(fullPath))
                {
                    Task<AnalysisResult> analysisResultTask =  visionServiceClient
                        .AnalyzeImageAsync(
                            imageFileStream, 
                            new []{ VisualFeature.Description, VisualFeature.Adult, VisualFeature.Tags}
                        );
                    analysisResultTask.Wait();

                    IEnumerable<string> tags = analysisResultTask.Result.Tags.Select(a => a.Name);
                    string caption = analysisResultTask.Result.Description.Captions.First().Text;
                    bool isAdult = analysisResultTask.Result.Adult.IsAdultContent;

                    media.SetTags("tags", tags, true);
                    media.SetValue("description", caption);
                    media.SetValue("isAdult", isAdult);
                }
            }
        }

        public static string GetImageFilePath(IMedia media)
        {
            try
            {
                string umbracoFile = media.GetValue<string>(Constants.Conventions.Media.File);
                return media.HasIdentity ? JsonConvert.DeserializeObject<ImageCropDataSet>(umbracoFile).Src : umbracoFile;
            }
            catch (Exception e)
            {
                return string.Empty;
            }
        }

    }
}