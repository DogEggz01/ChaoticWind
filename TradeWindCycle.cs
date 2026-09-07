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
        private static readonly Vector3 EastNorthEast =
            new Vector3(1f, 0f, 0.5f).normalized;
        private static readonly Vector3 EastSouthEast =
            new Vector3(1f, 0f, -0.5f).normalized;

        internal static Vector3 GetMidLatitudeDirection(float absoluteDay)
        {
            return GetAlternatingDirection(
                absoluteDay,
                NorthEast,
                SouthWest);
        }

        internal static Vector3 GetNorthernDirection(float absoluteDay)
        {
            return GetAlternatingDirection(
                absoluteDay,
                EastNorthEast,
                EastSouthEast);
        }

        private static Vector3 GetAlternatingDirection(
            float absoluteDay,
            Vector3 initialDirection,
            Vector3 alternateDirection)
        {
            absoluteDay = Mathf.Max(0f, absoluteDay);

            // The initial direction is dominant from days 0 through 19.
            if (absoluteDay < PhaseLengthDays)
            {
                return initialDirection;
            }

            int phase = Mathf.FloorToInt(absoluteDay / PhaseLengthDays);
            float dayWithinPhase = absoluteDay - phase * PhaseLengthDays;
            bool targetIsInitialDirection = phase % 2 == 0;

            Vector3 target = targetIsInitialDirection
                ? initialDirection
                : alternateDirection;
            if (dayWithinPhase >= TransitionLengthDays)
            {
                return target;
            }

            Vector3 previous = targetIsInitialDirection
                ? alternateDirection
                : initialDirection;
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
