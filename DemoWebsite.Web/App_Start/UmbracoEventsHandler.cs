using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DemoWebsite.Web.Helpers;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using Microsoft.ProjectOxford.Vision;
using Microsoft.ProjectOxford.Vision.Contract;
using Newtonsoft.Json;
using Umbraco.Core;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using Umbraco.Web;
using Umbraco.Web.Models;
using Face = Microsoft.ProjectOxford.Face.Contract.Face;
using File = System.IO.File;


namespace DemoWebsite.Web
{

    public class UmbracoEventsHandler : ApplicationEventHandler
    {

        private readonly string _visionApiKey = ConfigurationManager.AppSettings["VisionApiKey"];
        private readonly string _visionApiUrl = ConfigurationManager.AppSettings["VisionApiUrl"];

        private readonly string _faceApiKey = ConfigurationManager.AppSettings["FaceApiKey"];
        private readonly string _faceApiUrl = ConfigurationManager.AppSettings["FaceApiUrl"];
        private readonly string _faceApiGroup = ConfigurationManager.AppSettings["FaceApiGroup"];

        protected override void ApplicationStarted(UmbracoApplicationBase umbracoApplication, ApplicationContext applicationContext)
        {
            MediaService.Saving += MediaService_Saving;
            MemberService.Saving +=  MemberService_Saving;
        }

        private void MediaService_Saving(IMediaService sender, SaveEventArgs<Umbraco.Core.Models.IMedia> e)
        {

            var fs = FileSystemProviderManager.Current.GetFileSystemProvider<MediaFileSystem>();

            VisionServiceClient visionServiceClient = new VisionServiceClient(_visionApiKey, _visionApiUrl);

            FaceServiceClient faceServiceClient = new FaceServiceClient(_faceApiKey, _faceApiUrl);


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


                using (Stream imageFileStream = File.OpenRead(fullPath))
                {
                    var faces = AsyncHelpers.RunSync(() => faceServiceClient.DetectAsync(imageFileStream,true));

                    if (faces.Any())
                    {
                        Guid[] faceIds = faces.Select(a => a.FaceId).ToArray();
                        IdentifyResult[] results = AsyncHelpers.RunSync(() => faceServiceClient.IdentifyAsync(_faceApiGroup, faceIds, 5));

                        var matchedPersons = new List<IMember>();

                        foreach (IdentifyResult identifyResult in results)
                        {
                            foreach (var candidate in identifyResult.Candidates)
                            {
                                IEnumerable<IMember> searchResult = ApplicationContext.Current.Services.MemberService.GetMembersByPropertyValue("personId", candidate.PersonId.ToString());
                                matchedPersons.AddRange(searchResult);
                            }
                        }
                        if (matchedPersons.Any())
                        {
                            media.SetValue("persons", string.Join(",", matchedPersons.Select(a => a.GetUdi().ToString())));
                        }
                    }

                }
            }
        }


        private void MemberService_Saving(IMemberService sender, SaveEventArgs<IMember> e)
        {
            var fs = FileSystemProviderManager.Current.GetFileSystemProvider<MediaFileSystem>();
            var umbracoHelper = new UmbracoHelper(UmbracoContext.Current);

            var faceServiceClient = new FaceServiceClient(_faceApiKey, _faceApiUrl);

            // Try to create the face group
            try
            {
                faceServiceClient.CreatePersonGroupAsync(_faceApiGroup, _faceApiGroup);
            }
            catch (Exception)
            {
                // ignored
            }

            foreach (IMember member in e.SavedEntities)
            {
                if (!string.IsNullOrWhiteSpace(member.GetValue<string>("profilePicture")))
                {
                    var media = umbracoHelper.TypedMedia(member.GetValue("profilePicture"));
                    string fullPath = fs.GetFullPath(media.Url);

                    // Let's delete the person from the list
                    if(!string.IsNullOrWhiteSpace(member.GetValue<string>("personId")))
                    {
                        try
                        {
                            var personId = Guid.Parse(member.GetValue<string>("personId"));
                            AsyncHelpers.RunSync(() => faceServiceClient.DeletePersonAsync(_faceApiGroup, personId));
                        }
                        catch (Exception) {
                            // ignored 
                        }
                    }

                    // Detect face and attributes
                    using (Stream imageFileStream = File.OpenRead(fullPath))
                    {
                        Face[] detectface = AsyncHelpers.RunSync(
                            () => faceServiceClient.DetectAsync(imageFileStream,false,false,new []{ FaceAttributeType.Age, FaceAttributeType.Gender }));

                        member.SetValue("Age", detectface.First().FaceAttributes.Age.ToString());
                        member.SetValue("Gender", detectface.First().FaceAttributes.Gender);
                    }

                    // Create a person
                    CreatePersonResult person = AsyncHelpers.RunSync(() => faceServiceClient.CreatePersonAsync(_faceApiGroup, member.Name));
                    member.SetValue("personId", person.PersonId.ToString());

                    // Add face to person and make persistent
                    using (Stream imageFileStream = File.OpenRead(fullPath))
                    {
                        AddPersistedFaceResult result =
                            AsyncHelpers.RunSync(
                                () => faceServiceClient.AddPersonFaceAsync(_faceApiGroup, person.PersonId, imageFileStream));
                        member.SetValue("faceId", result.PersistedFaceId.ToString());
                    }

                    // train the facegroup
                    faceServiceClient.TrainPersonGroupAsync(_faceApiGroup);
                }
            }
        }





        private string GetImageFilePath(IMedia media)
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