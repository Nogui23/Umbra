namespace Umbra.Core;

/// <summary>
/// A person who uses the app. Profiles are common to every list: each one has its own favourites, progress and
/// «Seguir viendo» in each list, and its own preferred audio and subtitles.
/// </summary>
public sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Colour of the circle when there is no photo, as «#RRGGBB».</summary>
    public string Color { get; set; } = ProfileStore.Colors[0];
    /// <summary>Whether a photo was chosen; the picture is <see cref="ProfileStore.PhotoPath"/>.</summary>
    public bool HasPhoto { get; set; }
    /// <summary>Changes each time the photo does, so the picture shown is never an old one kept in memory.</summary>
    public int PhotoVersion { get; set; }
    /// <summary>Language of the last audio track chosen by hand (see <see cref="TrackLanguage"/>); empty for none.</summary>
    public string PreferredAudio { get; set; } = "";
    /// <summary>Same for subtitles, or <see cref="TrackLanguage.Off"/> when they were turned off.</summary>
    public string PreferredSubtitles { get; set; } = "";
}

public sealed class ProfileStore
{
    const string FileName = "profiles.json";

    /// <summary>Colours offered for the circles, chosen to read well with white text on the dark interface.</summary>
    public static readonly string[] Colors = ["#5E5CE6", "#BF5AF2", "#FF375F", "#FF9F0A", "#30D158", "#64D2FF", "#0A84FF", "#8E8E93"];

    public List<Profile> Items { get; private set; } = new();

    static string Dir => Path.Combine(AppPaths.DataDir, "profiles");

    public static string PhotoPath(Profile profile) => Path.Combine(Dir, profile.Id + ".png");

    /// <summary>File with what a profile has done in a list (or in the library, which is not a list).</summary>
    public static string DataFile(string accountId, string profileId) => $"userdata_{accountId}@{profileId}.json";

    /// <summary>
    /// Loads the profiles. The first time there are none: one is made and takes over everything saved before
    /// profiles existed, so nothing is lost.
    /// </summary>
    public void Load(Settings settings, string firstName)
    {
        Items = JsonStore.Load<List<Profile>>(FileName);
        if (Items.Count > 0) return;
        var first = new Profile
        {
            Name = firstName,
            PreferredAudio = settings.PreferredAudio,
            PreferredSubtitles = settings.PreferredSubtitles,
        };
        Items.Add(first);
        try
        {
            foreach (var file in Directory.GetFiles(AppPaths.DataDir, "userdata_*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Contains('@')) continue;
                var accountId = name["userdata_".Length..];
                File.Move(file, Path.Combine(AppPaths.DataDir, DataFile(accountId, first.Id)), overwrite: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron pasar los datos guardados al primer perfil", ex);
        }
        Save();
    }

    public void Save()
    {
        try { JsonStore.Save(FileName, Items); }
        catch (Exception ex) { Log.Error("No se pudieron guardar los perfiles", ex); }
    }

    public Profile Add(string name)
    {
        // Each new profile takes the next colour, so circles are told apart without a photo.
        var color = Colors.FirstOrDefault(c => Items.All(p => p.Color != c)) ?? Colors[Items.Count % Colors.Length];
        var profile = new Profile { Name = name, Color = color };
        Items.Add(profile);
        Save();
        return profile;
    }

    /// <summary>Deletes a profile with its photo and everything it did in every list. The last one cannot go.</summary>
    public void Remove(Profile profile)
    {
        if (Items.Count <= 1 || !Items.Remove(profile)) return;
        Save();
        try
        {
            foreach (var file in Directory.GetFiles(AppPaths.DataDir, $"userdata_*@{profile.Id}.json")) File.Delete(file);
            if (File.Exists(PhotoPath(profile))) File.Delete(PhotoPath(profile));
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron borrar los datos del perfil", ex);
        }
    }

    /// <summary>Keeps <paramref name="png"/> (already reduced) as the profile's photo.</summary>
    public void SetPhoto(Profile profile, byte[]? png)
    {
        try
        {
            if (png is null)
            {
                if (File.Exists(PhotoPath(profile))) File.Delete(PhotoPath(profile));
                profile.HasPhoto = false;
            }
            else
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(PhotoPath(profile), png);
                profile.HasPhoto = true;
            }
            profile.PhotoVersion++;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo guardar la foto del perfil", ex);
            throw new SourceException("No se pudo guardar la foto.");
        }
        Save();
    }

    /// <summary>Data files of every profile of a list, to delete them with the list.</summary>
    public static IEnumerable<string> FilesOfAccount(string accountId)
    {
        try { return Directory.GetFiles(AppPaths.DataDir, $"userdata_{accountId}@*.json"); }
        catch (Exception) { return []; }
    }
}
