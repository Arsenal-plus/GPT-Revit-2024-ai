// -----------------------------------------------------------------------------
// Horizun Core tests - original Horizun code.
//
// The compressed IFC GlobalId codec. Pure arithmetic, provable without a
// model: encode/decode must round-trip, and the one external reference
// vector this port was checked against before being written must still hold.
// -----------------------------------------------------------------------------
using System;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class IfcGuidCodecTests
    {
        [Fact]
        public void Encode_matches_the_reference_implementation_test_vector()
        {
            // hakonhc/IfcGuid's own test: this exact Guid -> this exact 22-char string.
            var guid = new Guid("01cf62c8-e9bc-bf88-0000-000000000005");
            Assert.Equal("01psB8wRo$Y00000000005", IfcGuidCodec.Encode(guid));
        }

        [Theory]
        [InlineData("01cf62c8-e9bc-bf88-0000-000000000005")]
        [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff")]
        public void Encode_then_decode_returns_the_original_guid(string guidText)
        {
            var guid = new Guid(guidText);
            string ifcGuid = IfcGuidCodec.Encode(guid);
            Assert.Equal(IfcGuidCodec.IfcGuidLength, ifcGuid.Length);
            Guid? decoded = IfcGuidCodec.Decode(ifcGuid);
            Assert.NotNull(decoded);
            Assert.Equal(guid, decoded.Value);
        }

        [Fact]
        public void Decode_of_a_random_guid_encoded_string_round_trips()
        {
            for (int i = 0; i < 50; i++)
            {
                Guid guid = Guid.NewGuid();
                Guid? decoded = IfcGuidCodec.Decode(IfcGuidCodec.Encode(guid));
                Assert.Equal(guid, decoded);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("tooShort")]
        [InlineData("01psB8wRo$Y0000000000512")]  // 24 chars, too long
        [InlineData("01psB8wRo Y00000000005!")]   // right length, illegal characters (space, '!')
        public void Decode_of_something_that_is_not_a_compressed_ifc_guid_is_null_not_invented(string text)
        {
            Assert.Null(IfcGuidCodec.Decode(text));
        }

        [Fact]
        public void First_character_of_an_encoded_guid_only_ever_takes_the_four_low_values()
        {
            // 22 base64 chars carry 132 bits for 128 significant ones: the first
            // character's top 6 bits are always the two padding bits plus four bits
            // that come from Data1's top byte, which itself is at most 0xFF - so the
            // first character can only ever be one of '0','1','2','3'.
            for (int i = 0; i < 20; i++)
            {
                string encoded = IfcGuidCodec.Encode(Guid.NewGuid());
                Assert.Contains(encoded[0], new[] { '0', '1', '2', '3' });
            }
        }
    }
}
