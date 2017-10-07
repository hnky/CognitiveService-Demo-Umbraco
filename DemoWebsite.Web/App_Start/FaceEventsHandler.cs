using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using DemoWebsite.Web.Helpers;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using Newtonsoft.Json;
using Umbraco.Core;
using Umbraco.Core.Events;
using Umbraco.Core.IO;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using Umbraco.Web;
using Umbraco.Web.Models;
using File = System.IO.File;


namespace DemoWebsite.Web
{

    public class FaceEventsHandler : ApplicationEventHandler
    {

        private readonly string _faceApiKey = ConfigurationManager.AppSettings["FaceApiKey"];
        private readonly string _faceApiUrl = ConfigurationManager.AppSettings["FaceApiUrl"];
        private readonly string _faceApiGroup = ConfigurationManager.AppSettings["FaceApiGroup"];

        private readonly MediaFileSystem _fs = FileSystemProviderManager.Current.GetFileSystemProvider<MediaFileSystem>();

        protected override void ApplicationStarted(UmbracoApplicationBase umbracoApplication, ApplicationContext applicationContext)
        {
            MemberService.Saving +=  MemberService_Saving;
            MemberService.Deleting += MemberService_Deleting;
            MediaService.Saving += MediaService_Saving;
            CreateFaceGroup();
        }


/* Stap 1 -> Face API: Try to create the face group */
        private void CreateFaceGroup()
        {
            try
            {
                var faceServiceClient = new FaceServiceClient(_faceApiKey, _faceApiUrl);
                var result = faceServiceClient.CreatePersonGroupAsync(_faceApiGroup, _faceApiGroup);
            }
            catch (Exception)
            {
                // ignored
            }
        }


        private void MemberService_Saving(IMemberService sender, SaveEventArgs<IMember> e)
        {
            var fs = FileSystemProviderManager.Current.GetFileSystemProvider<MediaFileSystem>();
            var umbracoHelper = new UmbracoHelper(UmbracoContext.Current);

            var faceServiceClient = new FaceServiceClient(_faceApiKey, _faceApiUrl);

            foreach (IMember member in e.SavedEntities)
            {
                if (!string.IsNullOrWhiteSpace(member.GetValue<string>("profilePicture")))
                {
                    var media = umbracoHelper.TypedMedia(member.GetValue("profilePicture"));
                    string fullPath = fs.GetFullPath(media.Url);

/* Stap 2  -> Face API: Delete the person if exists */
                    if (!string.IsNullOrWhiteSpace(member.GetValue<string>("personId")))
                    {
                        try
                        {
                            var personId = Guid.Parse(member.GetValue<string>("personId"));
                            AsyncHelpers.RunSync(() => faceServiceClient.DeletePersonAsync(_faceApiGroup, personId));
                        }
                        catch
                        {
                            // ignored
                        }
                    }

/* Stap 3 -> Face API: Detect face and attributes */
                    using (Stream imageFileStream = File.OpenRead(fullPath))
                    {
                        Face[] detectface = AsyncHelpers.RunSync(
                            () => faceServiceClient.DetectAsync(imageFileStream,
                            false,false,new []
                                {
                                    FaceAttributeType.Age,
                                    FaceAttributeType.Gender,
                                    FaceAttributeType.Glasses,
                                    FaceAttributeType.Makeup, 
                                    FaceAttributeType.Hair, 
                                }));

                        // Getting values and setting the properties on the member
                        string age = detectface.First().FaceAttributes.Age.ToString();
                        string gender = detectface.First().FaceAttributes.Gender;
                        string glasses = detectface.First().FaceAttributes.Glasses.ToString();
                        bool eyeMakeup = detectface.First().FaceAttributes.Makeup.EyeMakeup;
                        bool lipMakeup = detectface.First().FaceAttributes.Makeup.LipMakeup;

                        member.SetValue("Age", age);
                        member.SetValue("Gender", gender);
                        member.SetValue("glasses", glasses);
                        member.SetValue("eyeMakeup", eyeMakeup);
                        member.SetValue("lipMakeup", lipMakeup);
                    }

// ==> Stap 4 -> Create a person in the persongroup 
                    CreatePersonResult person = AsyncHelpers.RunSync(() =>
                        faceServiceClient.CreatePersonAsync(_faceApiGroup, member.Name));

                    member.SetValue("personId", person.PersonId.ToString());

// ==> Stap 5 -> Add face to person and make persistent 
                    using (Stream imageFileStream = File.OpenRead(fullPath))
                    {
                        AddPersistedFaceResult result =
                            AsyncHelpers.RunSync(
                                () => faceServiceClient.AddPersonFaceAsync(_faceApiGroup, person.PersonId, imageFileStream));
                        member.SetValue("faceId", result.PersistedFaceId.ToString());
                    }
                }
            }

// ==> Stap 6 -> Train the facegroup
            var trainingstask = faceServiceClient.TrainPersonGroupAsync(_faceApiGroup);
        }


/* ==> Stap 7 -> Detect faces and match them to Umbraco Members */
        private void MediaService_Saving(IMediaService sender, SaveEventArgs<IMedia> e)
        {
            FaceServiceClient faceServiceClient = new FaceServiceClient(_faceApiKey, _faceApiUrl);
            IMemberService memberService = ApplicationContext.Current.Services.MemberService;

            foreach (IMedia media in e.SavedEntities)
            {
                string relativeImagePath = ImagePathHelper.GetImageFilePath(media);
                string fullPath = _fs.GetFullPath(relativeImagePath);

                using (Stream imageFileStream = File.OpenRead(fullPath))
                {
                    var faces = AsyncHelpers.RunSync(() => 
                        faceServiceClient.DetectAsync(imageFileStream));

                    if (faces.Any())
                    {
                        Guid[] faceIds = faces.Select(a => a.FaceId).ToArray();
                        IdentifyResult[] results = AsyncHelpers.RunSync(() => 
                            faceServiceClient.IdentifyAsync(_faceApiGroup, faceIds, 5));

                        var matchedPersons = new List<IMember>();

                        foreach (IdentifyResult identifyResult in results)
                        {
                            foreach (var candidate in identifyResult.Candidates)
                            {
                                IEnumerable<IMember> searchResult = memberService.GetMembersByPropertyValue("personId", candidate.PersonId.ToString());
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


        private void MemberService_Deleting(IMemberService sender, DeleteEventArgs<IMember> deleteEventArgs)
        {
            var faceServiceClient = new FaceServiceClient(_faceApiKey, _faceApiUrl);

            foreach (IMember member in deleteEventArgs.DeletedEntities)
            {
                if (!string.IsNullOrWhiteSpace(member.GetValue<string>("personId")))
                {
                    try
                    {
                        var personId = Guid.Parse(member.GetValue<string>("personId"));
                        AsyncHelpers.RunSync(() => faceServiceClient.DeletePersonAsync(_faceApiGroup, personId));
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }
    }
}