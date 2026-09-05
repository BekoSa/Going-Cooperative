using System;
using System.Globalization;
using System.Text;

namespace GoingCooperative.Core
{
    public sealed class CombatProjectilePresentationState
    {
        public CombatProjectilePresentationState(
            long sequence,
            string attackerEntityId,
            double startX,
            double startY,
            double startZ,
            double destinationX,
            double destinationY,
            double destinationZ,
            double destinationOffsetX,
            double destinationOffsetY,
            double destinationOffsetZ,
            double speed,
            double archHeight,
            double destroyDelay,
            bool dontSpawnEffectOnTarget,
            bool fireEffect,
            bool bonusTrail,
            bool penaltyTrail,
            string particlesOnHit)
        {
            Sequence = sequence;
            AttackerEntityId = attackerEntityId ?? string.Empty;
            StartX = startX;
            StartY = startY;
            StartZ = startZ;
            DestinationX = destinationX;
            DestinationY = destinationY;
            DestinationZ = destinationZ;
            DestinationOffsetX = destinationOffsetX;
            DestinationOffsetY = destinationOffsetY;
            DestinationOffsetZ = destinationOffsetZ;
            Speed = speed;
            ArchHeight = archHeight;
            DestroyDelay = destroyDelay;
            DontSpawnEffectOnTarget = dontSpawnEffectOnTarget;
            FireEffect = fireEffect;
            BonusTrail = bonusTrail;
            PenaltyTrail = penaltyTrail;
            ParticlesOnHit = particlesOnHit ?? string.Empty;
        }

        public long Sequence { get; }
        public string AttackerEntityId { get; }
        public double StartX { get; }
        public double StartY { get; }
        public double StartZ { get; }
        public double DestinationX { get; }
        public double DestinationY { get; }
        public double DestinationZ { get; }
        public double DestinationOffsetX { get; }
        public double DestinationOffsetY { get; }
        public double DestinationOffsetZ { get; }
        public double Speed { get; }
        public double ArchHeight { get; }
        public double DestroyDelay { get; }
        public bool DontSpawnEffectOnTarget { get; }
        public bool FireEffect { get; }
        public bool BonusTrail { get; }
        public bool PenaltyTrail { get; }
        public string ParticlesOnHit { get; }
    }

    public static class CombatProjectilePresentationPayloads
    {
        public const string Prefix = "combat-projectile-v1";

        public static string Create(CombatProjectilePresentationState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return string.Join("|", new[]
            {
                Prefix,
                state.Sequence.ToString(CultureInfo.InvariantCulture),
                Encode(state.AttackerEntityId),
                Format(state.StartX),
                Format(state.StartY),
                Format(state.StartZ),
                Format(state.DestinationX),
                Format(state.DestinationY),
                Format(state.DestinationZ),
                Format(state.DestinationOffsetX),
                Format(state.DestinationOffsetY),
                Format(state.DestinationOffsetZ),
                Format(state.Speed),
                Format(state.ArchHeight),
                Format(state.DestroyDelay),
                state.DontSpawnEffectOnTarget ? "1" : "0",
                state.FireEffect ? "1" : "0",
                state.BonusTrail ? "1" : "0",
                state.PenaltyTrail ? "1" : "0",
                Encode(state.ParticlesOnHit)
            });
        }

        public static bool TryRead(
            string payload,
            out CombatProjectilePresentationState? state,
            out string error)
        {
            state = null;
            error = string.Empty;
            if (string.IsNullOrEmpty(payload))
            {
                error = "projectile-empty";
                return false;
            }

            var parts = payload.Split(
                new[] { '|' },
                StringSplitOptions.None);
            if (parts.Length != 20
                || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
            {
                error = "projectile-wire-version";
                return false;
            }

            if (!long.TryParse(
                    parts[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var sequence)
                || sequence <= 0L)
            {
                error = "projectile-sequence";
                return false;
            }

            try
            {
                var attackerEntityId = Decode(parts[2]);
                var particlesOnHit = Decode(parts[19]);
                if (string.IsNullOrWhiteSpace(attackerEntityId)
                    || attackerEntityId.Length > 128
                    || particlesOnHit.Length > 256)
                {
                    error = "projectile-text-bounds";
                    return false;
                }

                var numbers = new double[12];
                for (var i = 0; i < numbers.Length; i++)
                {
                    if (!TryFinite(parts[3 + i], out numbers[i]))
                    {
                        error = "projectile-number-" + i.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                }

                if (numbers[9] <= 0d
                    || numbers[9] > 1000d
                    || numbers[11] < 0d
                    || numbers[11] > 60d)
                {
                    error = "projectile-number-bounds";
                    return false;
                }

                if (!TryBool(parts[15], out var dontSpawnEffectOnTarget)
                    || !TryBool(parts[16], out var fireEffect)
                    || !TryBool(parts[17], out var bonusTrail)
                    || !TryBool(parts[18], out var penaltyTrail))
                {
                    error = "projectile-bool";
                    return false;
                }

                state = new CombatProjectilePresentationState(
                    sequence,
                    attackerEntityId,
                    numbers[0],
                    numbers[1],
                    numbers[2],
                    numbers[3],
                    numbers[4],
                    numbers[5],
                    numbers[6],
                    numbers[7],
                    numbers[8],
                    numbers[9],
                    numbers[10],
                    numbers[11],
                    dontSpawnEffectOnTarget,
                    fireEffect,
                    bonusTrail,
                    penaltyTrail,
                    particlesOnHit);
                return true;
            }
            catch (Exception ex)
            {
                error = "projectile-decode-" + ex.GetType().Name;
                return false;
            }
        }

        private static string Format(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool TryFinite(string value, out double parsed)
        {
            return double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed)
                && !double.IsNaN(parsed)
                && !double.IsInfinity(parsed);
        }

        private static bool TryBool(string value, out bool parsed)
        {
            if (value == "1")
            {
                parsed = true;
                return true;
            }

            if (value == "0")
            {
                parsed = false;
                return true;
            }

            parsed = false;
            return false;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(
                Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(
                Convert.FromBase64String(value));
        }
    }
}
