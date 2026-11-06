using System;
using System.Globalization;

namespace DriverScanTester.Utils
{
    /// <summary>
    /// Parses and formats the optional horizontal camera yaw stored on a path waypoint.
    /// The runtime always receives the exact 32-bit float bit pattern at +0x1A8, so a
    /// value captured from the game (or typed as radians/degrees/raw bits) round-trips
    /// without precision loss.
    ///
    /// The editor shows the value the same way the address list (C_CameraHorizontal at
    /// [Ares.exe + 0x4883F0] + 0x1A8) shows it: the raw 4-byte value, e.g. 1070780928 /
    /// 0x3FD2D200. Radians are never the stored/displayed form.
    ///
    /// Accepted input (blank = no start angle):
    ///   1070165538          raw 4-byte value, exactly as the address list / Cheat Engine shows it
    ///   0x3FC96E22          raw 32-bit camera-yaw bit pattern
    ///   90deg / 90°         degrees, converted to radians
    ///   1.5736735rad        radians (explicit "rad" suffix, or a decimal point)
    /// </summary>
    public static class CameraAngleValue
    {
        /// <summary>
        /// Parses the editor text. Returns true for a valid value or for an empty
        /// string (which means "no start angle"). On invalid text the previous value
        /// must be kept by returning false.
        /// </summary>
        public static bool TryParse(string? text, out int? angleBits)
        {
            angleBits = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            string value = text.Trim();

            // Raw bit pattern: 0x3FC96E22
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(value.Substring(2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out uint hexBits))
                {
                    angleBits = unchecked((int)hexBits);
                    return true;
                }
                return false;
            }

            bool isDegrees = false;
            bool isRadians = false;
            if (value.EndsWith("deg", StringComparison.OrdinalIgnoreCase))
            {
                isDegrees = true;
                value = value.Substring(0, value.Length - 3).Trim();
            }
            else if (value.EndsWith("°", StringComparison.Ordinal))
            {
                isDegrees = true;
                value = value.Substring(0, value.Length - 1).Trim();
            }
            else if (value.EndsWith("rad", StringComparison.OrdinalIgnoreCase))
            {
                isRadians = true;
                value = value.Substring(0, value.Length - 3).Trim();
            }

            if (value.Length == 0)
                return false;

            if (!TryParseNumber(value, out double number))
                return false;

            // No suffix: infer the intended unit.
            //   - decimal notation (contains '.' / exponent) => radians, matching GetCameraAngle logs
            //   - integer 0..360                              => degrees
            //   - any other integer                            => raw bit pattern
            if (!isDegrees && !isRadians)
            {
                bool hasDecimalPoint = value.IndexOf('.') >= 0 ||
                                       value.IndexOf('e') >= 0 ||
                                       value.IndexOf('E') >= 0;

                if (hasDecimalPoint)
                {
                    isRadians = true;
                }
                else if (number >= 0d && number <= 360d && Math.Floor(number) == number)
                {
                    isDegrees = true;
                }
                else if (Math.Floor(number) == number &&
                         number >= int.MinValue && number <= int.MaxValue)
                {
                    angleBits = unchecked((int)number);
                    return true;
                }
                else
                {
                    isRadians = true;
                }
            }

            double radians = isDegrees
                ? number * Math.PI / 180d
                : number;

            if (double.IsNaN(radians) || double.IsInfinity(radians))
                return false;

            angleBits = BitConverter.SingleToInt32Bits((float)radians);
            return true;
        }

        /// <summary>
        /// Formats the stored bit pattern the way the address list shows the field:
        /// the raw 4-byte value as a decimal integer (e.g. 1070780928), not radians.
        /// Parsing this text yields the identical bit pattern again.
        /// </summary>
        public static string Format(int angleBits)
        {
            return angleBits.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Human-readable decode shown next to the editor field.</summary>
        public static string Describe(int? angleBits)
        {
            if (!angleBits.HasValue)
                return "No start angle — the camera is left untouched.";

            int bits = angleBits.Value;
            float radians = BitConverter.Int32BitsToSingle(bits);
            double degrees = radians * 180d / Math.PI;
            degrees %= 360d;
            if (degrees < 0d)
                degrees += 360d;

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} (0x{1:X8}) = {2:F2}\u00B0 = {3} rad",
                bits,
                bits,
                degrees,
                radians.ToString("R", CultureInfo.InvariantCulture));
        }

        private static bool TryParseNumber(string text, out double value)
        {
            const NumberStyles style = NumberStyles.Float;
            return double.TryParse(text, style, CultureInfo.InvariantCulture, out value) ||
                   double.TryParse(text, style, CultureInfo.CurrentCulture, out value);
        }
    }
}
