using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Deezer
{
    public class DeezerIndexerSettingsValidator : AbstractValidator<DeezerIndexerSettings>
    {
    }

    public class DeezerIndexerSettings : IIndexerSettings
    {
        private static readonly DeezerIndexerSettingsValidator Validator = new DeezerIndexerSettingsValidator();

        [FieldDefinition(0, Label = "Arl", Type = FieldType.Textbox)]
        public string Arl { get; set; } = "";

        [FieldDefinition(1, Label = "Hide Albums With Missing Tracks", HelpText = "If an album has any unavailable tracks on Deezer, they will not be provided when searching.", Type = FieldType.Checkbox)]
        public bool HideAlbumsWithMissing { get; set; } = true;

        [FieldDefinition(2, Label = "Only Best Available Quality", HelpText = "Show one row per album: the highest bitrate Deezer offers and your account entitles. Lossless > MP3 320 > MP3 128. Recommended on — Lidarr's quality profile picks the best anyway, so the lower-bitrate rows are just noise.", Type = FieldType.Checkbox)]
        public bool OnlyBestQuality { get; set; } = true;

        [FieldDefinition(3, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        // this is hardcoded so this doesn't need to exist except that it's required by the interface
        public string BaseUrl { get; set; } = "";

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
