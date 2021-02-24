using System.Text.Json;

namespace Synthesis.Bethesda.GUI
{
    public class Int8SettingsVM : BasicSettingsVM<sbyte>
    {
        public Int8SettingsVM(SettingsMeta memberName, object? defaultVal)
            : base(memberName, defaultVal)
        {
        }

        public Int8SettingsVM()
            : base(SettingsMeta.Empty, default)
        {
        }

        public override sbyte Get(JsonElement property) => property.GetSByte();

        public override sbyte GetDefault() => default(sbyte);

        public override SettingsNodeVM Duplicate() => new Int8SettingsVM(Meta, DefaultValue);
    }
}
