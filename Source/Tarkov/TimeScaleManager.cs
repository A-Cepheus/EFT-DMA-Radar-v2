using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace eft_dma_radar.Source.Tarkov
{
    internal static class TimeScaleManager
    {
        internal static ulong address;
        internal static bool working;

        private static float safeValue = 1.8f;
        private static float defaultValue = 1f;

        internal static ulong GetTimeScale(ulong unityBase)
        {
            var _address = Memory.ReadValue<ulong>(unityBase + Offsets.ModuleBase.TimeScale + 7 * 8);
            address = _address;
            return _address;
        }

        internal static void SetTimeScale()
        {
            Memory.WriteValue<float>(address + Offsets.TimeScale.Value, safeValue);
            working = true;
        }

        internal static void ResetTimeScale()
        {
            var value = Memory.ReadValue<float>(address + Offsets.TimeScale.Value);

            if (value != defaultValue)
            {
                Memory.WriteValue<float>(address + Offsets.TimeScale.Value, defaultValue);
            }

            working = false;
        }

        internal static void EnableTimeScale(bool on)
        {
            if (on)
                SetTimeScale();
            else
                ResetTimeScale();
        }
    }
}
