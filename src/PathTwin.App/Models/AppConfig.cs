namespace PathTwin.App.Models;

public sealed class AppConfig
{
    public string ActiveProfileId { get; set; } = "default";
    public List<ProfileConfig> Profiles { get; set; } = [ProfileConfig.CreateDefault()];

    public ProfileConfig ActiveProfile
    {
        get
        {
            var profile = Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);
            if (profile is not null)
            {
                return profile;
            }

            if (Profiles.Count == 0)
            {
                var defaultProfile = ProfileConfig.CreateDefault();
                Profiles.Add(defaultProfile);
                ActiveProfileId = defaultProfile.Id;
                return defaultProfile;
            }

            ActiveProfileId = Profiles[0].Id;
            return Profiles[0];
        }
    }
}
