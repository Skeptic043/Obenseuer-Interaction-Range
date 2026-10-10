using System;
using BepInEx.Configuration;

namespace InteractionRange
{
    internal static class RangePolicy
    {
        internal const float DefaultMultiplier = 1.2f;

        internal static float Normalize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return DefaultMultiplier;
            value = Math.Max(1f, Math.Min(2f, value));
            return (float)Math.Round(value * 5f, MidpointRounding.AwayFromZero) / 5f;
        }

        internal static float UseDistance(float nativeDistance, float multiplier)
            => multiplier == 1f ? nativeDistance : nativeDistance * multiplier;
    }

    internal sealed class RangeSettings : IDisposable
    {
        internal ConfigEntry<float> Multiplier { get; }
        private bool normalizing;

        internal RangeSettings(ConfigFile config)
        {
            Multiplier = config.Bind("General", "Range multiplier", RangePolicy.DefaultMultiplier,
                new ConfigDescription("Normal interaction reach. Snaps to 1x, 1.2x, 1.4x, 1.6x, 1.8x or 2x. Build/cleaning mode, melee and item placement stay unchanged.",
                    new AcceptableValueRange<float>(1f, 2f)));
            Multiplier.SettingChanged += SettingChanged;
            Normalize();
        }

        private void SettingChanged(object sender, EventArgs args)
        {
            if (!normalizing) Normalize();
        }

        private void Normalize()
        {
            float normalized = RangePolicy.Normalize(Multiplier.Value);
            if (Multiplier.Value.Equals(normalized)) return;
            normalizing = true;
            try { Multiplier.Value = normalized; }
            finally { normalizing = false; }
        }

        public void Dispose() => Multiplier.SettingChanged -= SettingChanged;
    }
}
