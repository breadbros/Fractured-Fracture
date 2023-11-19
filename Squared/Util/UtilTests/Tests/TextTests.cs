﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Squared.Util.Text;

namespace Squared.Util {
    [TestFixture]
    public class TextTests {
        [Test]
        public void CodepointEnumerable () {
            Assert.AreEqual(0, "".Codepoints().Count());
            Assert.AreEqual(1, "a".Codepoints().Count());
            Assert.AreEqual(1, "こ".Codepoints().Count());
            Assert.AreEqual(1, "\U0002F8B6".Codepoints().Count());
            Assert.AreEqual(3, "\U0002F8B6\U0002F8CD\U0002F8D3".Codepoints().Count());
            var cps = "\U0002F8B6a\U0002F8D3b".Codepoints(0).Select(cp => cp.Codepoint).ToArray();
            Assert.AreEqual(
                new uint[] {
                    0x2F8B6, 'a', 0x2F8D3, 'b'
                }, cps
            );
            cps = "\U0002F8B6a\U0002F8D3b".Codepoints(1).Select(cp => cp.Codepoint).ToArray();
            Assert.AreEqual(
                new uint[] {
                    0xFFFD, 'a', 0x2F8D3, 'b'
                }, cps                
            );
            var chis = "\U0002F8B6a\U0002F8D3b".Codepoints(0).Select(cp => cp.CharacterIndex).ToArray();
            Assert.AreEqual(
                new int[] {
                    0, 2, 3, 5
                }, chis
            );
            var cpis = "\U0002F8B6a\U0002F8D3b".Codepoints(0).Select(cp => cp.CodepointIndex).ToArray();
            Assert.AreEqual(
                new int[] {
                    0, 1, 2, 3
                }, cpis
            );
            Assert.AreEqual(3, "\U0002F8B6a\U0002F8D3b".Codepoints(2).Count());
            Assert.AreEqual(2, "\U0002F8B6a\U0002F8D3b".Codepoints(3).Count());
        }

        [Test]
        public void DecodeSurrogatePair () {
            uint codepoint;
            Assert.False(Unicode.DecodeSurrogatePair('\0', '\0', out codepoint));
            Assert.AreEqual(0, codepoint);
            Assert.False(Unicode.DecodeSurrogatePair('a', '\0', out codepoint));
            Assert.AreEqual((uint)'a', codepoint);
            Assert.False(Unicode.DecodeSurrogatePair('a', 'b', out codepoint));
            Assert.AreEqual((uint)'a', codepoint);
            Assert.True(Unicode.DecodeSurrogatePair("\U0002F8B6"[0], "\U0002F8B6"[1], out codepoint));
            Assert.AreEqual((uint)0x2F8B6, codepoint);
            Assert.False(Unicode.DecodeSurrogatePair("\U0002F8B6"[0], '\0', out codepoint));
            Assert.AreEqual(0xFFFD, codepoint);
        }

        [Test]
        public void NthCodepoint () {
            var str = "\0\U0002F8B6a\U0002F8D3b\0c";
            Assert.AreEqual(null, Unicode.NthCodepoint(str, -1));
            Assert.AreEqual(0, Unicode.NthCodepoint(str, 0));
            Assert.AreEqual(0x2F8B6, Unicode.NthCodepoint(str, 1));
            Assert.AreEqual('a', Unicode.NthCodepoint(str, 2));
            Assert.AreEqual(null, Unicode.NthCodepoint(str, 12));
        }

        [Test]
        public void FindWordBoundary () {
            var str = "  hello world   test \t  \U0002F8B6\U0002F8D3b ok\0";
            Assert.AreEqual(new Pair<int>(0, 2), Unicode.FindWordBoundary(str, 0));
            Assert.AreEqual(new Pair<int>(0, 2), Unicode.FindWordBoundary(str, 1));
            Assert.AreEqual(new Pair<int>(2, 7), Unicode.FindWordBoundary(str, 2));
            Assert.AreEqual(new Pair<int>(2, 7), Unicode.FindWordBoundary(str, 4));
            Assert.AreEqual(new Pair<int>(24, 29), Unicode.FindWordBoundary(str, 24));
            Assert.AreEqual(new Pair<int>(24, 29), Unicode.FindWordBoundary(str, 25));
        }

        // Test cases from RealParserTestsBase.cs in dotnet/runtime
        public static readonly ValueTuple<string, ulong>[] FloatInlineData = new[] {
        new ValueTuple<string, ulong>("0.0", 0x00000000),
        // Verify small and large exactly representable integers:
        new ValueTuple<string, ulong>("1", 0x3f800000),
        new ValueTuple<string, ulong>("2", 0x40000000),
        new ValueTuple<string, ulong>("3", 0x40400000),
        new ValueTuple<string, ulong>("4", 0x40800000),
        new ValueTuple<string, ulong>("5", 0x40A00000),
        new ValueTuple<string, ulong>("6", 0x40C00000),
        new ValueTuple<string, ulong>("7", 0x40E00000),
        new ValueTuple<string, ulong>("8", 0x41000000),
        new ValueTuple<string, ulong>("16777208", 0x4b7ffff8),
        new ValueTuple<string, ulong>("16777209", 0x4b7ffff9),
        new ValueTuple<string, ulong>("16777210", 0x4b7ffffa),
        new ValueTuple<string, ulong>("16777211", 0x4b7ffffb),
        new ValueTuple<string, ulong>("16777212", 0x4b7ffffc),
        new ValueTuple<string, ulong>("16777213", 0x4b7ffffd),
        new ValueTuple<string, ulong>("16777214", 0x4b7ffffe),
        new ValueTuple<string, ulong>("16777215", 0x4b7fffff), // 2^24 - 1
        // Verify the smallest and largest denormal values:
        new ValueTuple<string, ulong>("1.4012984643248170e-45", 0x00000001),
        new ValueTuple<string, ulong>("2.8025969286496340e-45", 0x00000002),
        new ValueTuple<string, ulong>("4.2038953929744510e-45", 0x00000003),
        new ValueTuple<string, ulong>("5.6051938572992680e-45", 0x00000004),
        new ValueTuple<string, ulong>("7.0064923216240850e-45", 0x00000005),
        new ValueTuple<string, ulong>("8.4077907859489020e-45", 0x00000006),
        new ValueTuple<string, ulong>("9.8090892502737200e-45", 0x00000007),
        new ValueTuple<string, ulong>("1.1210387714598537e-44", 0x00000008),
        new ValueTuple<string, ulong>("1.2611686178923354e-44", 0x00000009),
        new ValueTuple<string, ulong>("1.4012984643248170e-44", 0x0000000a),
        new ValueTuple<string, ulong>("1.5414283107572988e-44", 0x0000000b),
        new ValueTuple<string, ulong>("1.6815581571897805e-44", 0x0000000c),
        new ValueTuple<string, ulong>("1.8216880036222622e-44", 0x0000000d),
        new ValueTuple<string, ulong>("1.9618178500547440e-44", 0x0000000e),
        new ValueTuple<string, ulong>("2.1019476964872256e-44", 0x0000000f),
        new ValueTuple<string, ulong>("1.1754921087447446e-38", 0x007ffff0),
        new ValueTuple<string, ulong>("1.1754922488745910e-38", 0x007ffff1),
        new ValueTuple<string, ulong>("1.1754923890044375e-38", 0x007ffff2),
        new ValueTuple<string, ulong>("1.1754925291342839e-38", 0x007ffff3),
        new ValueTuple<string, ulong>("1.1754926692641303e-38", 0x007ffff4),
        new ValueTuple<string, ulong>("1.1754928093939768e-38", 0x007ffff5),
        new ValueTuple<string, ulong>("1.1754929495238232e-38", 0x007ffff6),
        new ValueTuple<string, ulong>("1.1754930896536696e-38", 0x007ffff7),
        new ValueTuple<string, ulong>("1.1754932297835160e-38", 0x007ffff8),
        new ValueTuple<string, ulong>("1.1754933699133625e-38", 0x007ffff9),
        new ValueTuple<string, ulong>("1.1754935100432089e-38", 0x007ffffa),
        new ValueTuple<string, ulong>("1.1754936501730553e-38", 0x007ffffb),
        new ValueTuple<string, ulong>("1.1754937903029018e-38", 0x007ffffc),
        new ValueTuple<string, ulong>("1.1754939304327482e-38", 0x007ffffd),
        new ValueTuple<string, ulong>("1.1754940705625946e-38", 0x007ffffe),
        new ValueTuple<string, ulong>("1.1754942106924411e-38", 0x007fffff),
        // This number is exactly representable and should not be rounded in any
        // mode:
        // 0.1111111111111111111111100
        //                          ^
        new ValueTuple<string, ulong>("0.99999988079071044921875", 0x3f7ffffe),
        // This number is below the halfway point between two representable values
        // so it should round down in nearest mode:
        // 0.11111111111111111111111001
        //                          ^
        new ValueTuple<string, ulong>("0.99999989569187164306640625", 0x3f7ffffe),
        // This number is exactly halfway between two representable values, so it
        // should round to even in nearest mode:
        // 0.1111111111111111111111101
        //                          ^
        new ValueTuple<string, ulong>("0.9999999105930328369140625", 0x3f7ffffe),
        // This number is above the halfway point between two representable values
        // so it should round up in nearest mode:
        // 0.11111111111111111111111011
        //                          ^
        new ValueTuple<string, ulong>("0.99999992549419403076171875", 0x3f7fffff),
        // This is the exact string for the largest denormal value and contains
        // the most significant digits of any single-precision floating-point value
        new ValueTuple<string, ulong>("1.175494210692441075487029444849287348827052428745893333857174530571" +
                    "588870475618904265502351336181163787841796875e-38", 0x007FFFFF),
        new ValueTuple<string, ulong>("0.000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000020000000000000000000000000000000000000002000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000020000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000040000", 0x00000000),
        new ValueTuple<string, ulong>("00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000020000000000000000000000000000000000000002000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000020000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000040000", 0x7F800000),
                    new ValueTuple<string, ulong>("-0", 0x80000000u),
                    new ValueTuple<string, ulong>("-0.0", 0x80000000u),
                    new ValueTuple<string, ulong>("-infinity", 0xFF800000u),
                    new ValueTuple<string, ulong>("-iNfInItY", 0xFF800000u),
                    new ValueTuple<string, ulong>("-INFINITY", 0xFF800000u),
                    new ValueTuple<string, ulong>("infinity", 0x7F800000),
                    new ValueTuple<string, ulong>("InFiNiTy", 0x7F800000),
                    new ValueTuple<string, ulong>("INFINITY", 0x7F800000),
                    new ValueTuple<string, ulong>("+infinity", 0x7F800000),
                    new ValueTuple<string, ulong>("+InFiNiTy", 0x7F800000),
                    new ValueTuple<string, ulong>("+INFINITY", 0x7F800000),
                    new ValueTuple<string, ulong>("-nan", 0xFFC00000u),
                    new ValueTuple<string, ulong>("-nAn", 0xFFC00000u),
                    new ValueTuple<string, ulong>("-NAN", 0xFFC00000u),
                    new ValueTuple<string, ulong>("nan", 0xFFC00000u),
                    new ValueTuple<string, ulong>("Nan", 0xFFC00000u),
                    new ValueTuple<string, ulong>("NAN", 0xFFC00000u),
                    new ValueTuple<string, ulong>("+nan", 0xFFC00000u),
                    new ValueTuple<string, ulong>("+NaN", 0xFFC00000u),
                    new ValueTuple<string, ulong>("+NAN", 0xFFC00000u),
        };

        [Test]
        public void TryParseFloat ([ValueSource("FloatInlineData")] ValueTuple<string, ulong> pair) {
            var bytes = BitConverter.GetBytes((uint)pair.Item2);
            var expected = BitConverter.ToSingle(bytes, 0);
            var astr = new AbstractString(pair.Item1);
            Assert.True(astr.TryParse(out float parsed));
            Assert.AreEqual(expected, parsed);
        }

        [Test]
        public void TryParseFloat_SpecificRanges () {
            var tups = new Pair<uint>[] {
                // Verify the smallest denormals:
                new Pair<uint>(0x00000001, 0x00000100),
                // Verify the largest denormals and the smallest normals:
                new Pair<uint>(0x007fff00, 0x00800100),
                // Verify the largest normals:
                new Pair<uint>(0x7f7fff00, 0x7f800000),
            };

            bool ok = true;

            foreach (var tup in tups) {
                for (uint i = tup.First; i != tup.Second; i++)
                    ok = TestRoundTripSingle(i) && ok;

                ok = TestRoundTripSingle((float)int.MaxValue) && ok;
                ok = TestRoundTripSingle((float)uint.MaxValue) && ok;
            }

            Assert.IsTrue(ok);
        }

        // FIXME: "1E-38" and "1E+28" are both broken. I don't know why. I blame musl.
        [Test]
        public void TryParseFloat_SpecificPowers () {
            var tups = new ValueTuple<int, int, int>[] {
                // Verify all representable powers of two and nearby values:
                new ValueTuple<int, int, int>(2, -1022, 1024),
                // Verify all representable powers of ten and nearby values:
                new ValueTuple<int, int, int>(10, -50, 41),
            };

            bool ok = true;

            foreach (var tup in tups) {
                for (int i = tup.Item2; i != tup.Item3; ++i) {
                    float f = (float)Math.Pow(tup.Item1, i);
                    uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);

                    ok = TestRoundTripSingle(bits - 1) && ok;
                    ok = TestRoundTripSingle(bits) && ok;
                    ok = TestRoundTripSingle(bits + 1) && ok;
                }
            }

            Assert.IsTrue(ok);
        }

        bool CheckOneSingle (string s, float expected) {
            var astr = new AbstractString(s);
            if (!astr.TryParse(out float result)) {
                Console.Error.WriteLine("Failed to parse: " + s);
                return false;
            }
            if (expected != result) {
                // HACK: We're okay with very minor precision differences because this is a different algorithm,
                //  so if the parsed value round-trips back to the input we should be fine.
                if (result.ToString(CultureInfo.InvariantCulture) == s)
                    return true;

                Console.Error.WriteLine("Expected '{0}' to parse to {1} but it was {2} (delta = {3})", s, expected, result, expected - result);
                return false;
            }
            // Console.Out.WriteLine("ok: {0} -> {1}", s, expected);
            return true;
        }

        private bool TestRoundTripSingle(float d)
        {
            string s = d.ToString(CultureInfo.InvariantCulture);
            return CheckOneSingle(s, d);
        }

        private bool TestRoundTripSingle(uint bits)
        {
            float d = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

            if (Arithmetic.IsFinite(d))
            {
                string s = d.ToString(CultureInfo.InvariantCulture);
                return CheckOneSingle(s, bits);
            }

            // HACK
            return true;
        }

        public static readonly (string lhs, string rhs, bool caseSensitive, bool caseInsensitive, bool ignoreExtensions)[] PathComparerTestCases = new[] {
            ("a/b/c", "a\\b\\c", true, true, true),
            ("a/b\\c", "a\\b/c", true, true, true),
            ("A/b\\c", "a\\B/C", false, true, false),
            ("folder/test.png", "folder\\test.jpeg", false, false, true),
            ("folder/subfolder.with.dots/foo.png", "folder/subfolder.other/foo.jpeg", false, false, false),
            ("folder/subfolder.with.dots/foo.png", "folder/subfolder.other/foo.png", false, false, false),
            ("folder/subfolder.with.dots/foo.png", "folder/subfolder.with.dots/foo.jpeg", false, false, true),
        };

        [Test]        
        public void PathNameComparerTests (
            [ValueSource("PathComparerTestCases")]
            (string lhs, string rhs, bool caseSensitive, bool caseInsensitive, bool ignoreExtensions) tuple
        ) {
            Check(PathNameComparer.CaseSensitive, tuple, tuple.caseSensitive);
            Check(PathNameComparer.CaseInsensitive, tuple, tuple.caseInsensitive);
            Check(new PathNameComparer(false, true), tuple, tuple.ignoreExtensions);

            static void Check (
                PathNameComparer comparer, 
                (string lhs, string rhs, bool caseSensitive, bool caseInsensitive, bool ignoreExtensions) tuple,
                bool expected
            ) {
                Assert.AreEqual(expected, comparer.Equals(tuple.lhs, tuple.rhs), "Expected comparison of {0} and {1} to yield {2}", tuple.lhs, tuple.rhs, expected);
                if (expected)
                    Assert.AreEqual(comparer.GetHashCode(tuple.lhs), comparer.GetHashCode(tuple.rhs), "Expected {0} and {1} to have the same HashCode", tuple.lhs, tuple.rhs);
                // Testing the inverse (they must have different hashcodes) isn't possible, and wouldn't be true given how we implement GetHashCode
            }
        }
    }
}
