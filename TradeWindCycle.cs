using UnityEngine;

namespace ChaoticWind
{
    internal static class TradeWindCycle
    {
        internal const float PhaseLengthDays = 20f;
        internal const float TransitionLengthDays = 1f;
        internal const int TransitionRetargetSteps = 20;

        private static readonly Vector3 NorthEast =
            new Vector3(0.75f, 0f, 0.75f).normalized;
        private static readonly Vector3 SouthWest =
            new Vector3(-1f, 0f, -0.5f).normalized;

        internal static Vector3 GetDirection(float absoluteDay)
        {
            absoluteDay = Mathf.Max(0f, absoluteDay);

            // The initial period is fully NE: days 0 through 19.
            if (absoluteDay < PhaseLengthDays)
            {
                return NorthEast;
            }

            int phase = Mathf.FloorToInt(absoluteDay / PhaseLengthDays);
            float dayWithinPhase = absoluteDay - phase * PhaseLengthDays;
            bool targetIsNorthEast = phase % 2 == 0;

            Vector3 target = targetIsNorthEast ? NorthEast : SouthWest;
            if (dayWithinPhase >= TransitionLengthDays)
            {
                return target;
            }

            Vector3 previous = targetIsNorthEast ? SouthWest : NorthEast;
            float transition = Mathf.SmoothStep(
                0f,
                1f,
                dayWithinPhase / TransitionLengthDays);
            return Vector3.Slerp(previous, target, transition).normalized;
        }

        internal static int GetRetargetSample(float absoluteDay)
        {
            absoluteDay = Mathf.Max(0f, absoluteDay);

            if (absoluteDay < PhaseLengthDays)
            {
                return TransitionRetargetSteps;
            }

            int phase = Mathf.FloorToInt(absoluteDay / PhaseLengthDays);
            float dayWithinPhase = absoluteDay - phase * PhaseLengthDays;
            int state = TransitionRetargetSteps;

            if (dayWithinPhase < TransitionLengthDays)
            {
                float progress = Mathf.Clamp01(
                    dayWithinPhase / TransitionLengthDays);
                state = Mathf.Min(
                    Mathf.FloorToInt(progress * TransitionRetargetSteps),
                    TransitionRetargetSteps - 1);
            }

            return phase * (TransitionRetargetSteps + 1) + state;
        }
    }
}
