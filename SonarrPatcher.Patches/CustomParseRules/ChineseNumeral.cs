using System.Collections.Generic;

namespace SonarrPatcher.Patches
{
    /// <summary>
    /// Converts Chinese numerals up to the tens place (1-99): single characters
    /// (一二三四五六七八九 / 壹贰叁肆伍陆柒捌玖) and 十/拾 compounds such as 十=10,
    /// 十一=11, 二十=20, 二十三=23, 壹拾=10, 九十九=99. Anything else (零, 百/千/万,
    /// 两, multi-character strings without a ten marker, any other Han character)
    /// is not handled.
    /// </summary>
    internal static class ChineseNumeral
    {
        public static bool TryParse(string input, out int result)
        {
            result = 0;

            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var text = input.Trim();

            if (text.Length == 0)
            {
                return false;
            }

            var tenIndex = -1;

            for (var i = 0; i < text.Length; i++)
            {
                if (IsTen(text[i]))
                {
                    tenIndex = i;
                    break;
                }
            }

            var tens = 0;
            var ones = 0;

            if (tenIndex == -1)
            {
                if (text.Length != 1 || !Values.TryGetValue(text[0], out ones))
                {
                    return false;
                }
            }
            else if (tenIndex == 0)
            {
                // 十 / 十X
                tens = 1;

                if (text.Length > 2 || (text.Length == 2 && !Values.TryGetValue(text[1], out ones)))
                {
                    return false;
                }
            }
            else if (tenIndex == 1)
            {
                // X十 / X十Y
                if (!Values.TryGetValue(text[0], out tens))
                {
                    return false;
                }

                if (text.Length == 3)
                {
                    if (!Values.TryGetValue(text[2], out ones))
                    {
                        return false;
                    }
                }
                else if (text.Length != 2)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            result = (tens * 10) + ones;

            return result >= 1 && result <= 99;
        }

        private static bool IsTen(char c)
        {
            return c == '十' || c == '拾';
        }

        private static readonly Dictionary<char, int> Values = new Dictionary<char, int>
        {
            ['一'] = 1, ['二'] = 2, ['三'] = 3, ['四'] = 4, ['五'] = 5,
            ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9,
            ['壹'] = 1, ['贰'] = 2, ['叁'] = 3, ['肆'] = 4, ['伍'] = 5,
            ['陆'] = 6, ['柒'] = 7, ['捌'] = 8, ['玖'] = 9
        };
    }
}
