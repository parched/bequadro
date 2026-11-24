using System.Runtime.CompilerServices;
using Bequadro.Tests.Layout;

namespace Bequadro.Tests.Layout
{
    /// <summary>
    /// Consumer fields beyond the schema's, which make the struct larger than its schema size (API-004).
    /// </summary>
    public partial struct Small
    {
        private int _extra;
        private long _more;

        public int Extra
        {
            readonly get => _extra;
            set => _extra = value;
        }

        public long More
        {
            readonly get => _more;
            set => _more = value;
        }
    }
}

namespace Bequadro.Tests
{
    /// <summary>
    /// API-004 and API-006: in a union whose struct members overlap, a struct member with consumer fields keeps all of
    /// its fields, and the union keeps its kind.
    /// </summary>
    public class LayoutTests
    {
        private static readonly Small Value = new()
        {
            X = 0x55667788,
            Extra = 0x11223344,
            More = -1,
        };

        [Fact]
        public void Union_WithTable_KeepsConsumerFieldsAndKind()
        {
            WithTable union = Value;

            union.Kind.Should().Be(WithTable.ItemKind.Small);
            (union.Small.X, union.Small.Extra, union.Small.More).Should().Be((Value.X, Value.Extra, Value.More));
            new WithTable(new Wide { Y = -1 }).Wide.Y.Should().Be(-1);
        }

        [Fact]
        public void Union_StructsOnly_KeepsConsumerFieldsAndKind()
        {
            StructsOnly union = Value;

            union.Kind.Should().Be(StructsOnly.ItemKind.Small);
            (union.Small.X, union.Small.Extra, union.Small.More).Should().Be((Value.X, Value.Extra, Value.More));
            new StructsOnly(new Wide { Y = -1 }).Wide.Y.Should().Be(-1);
        }

        [Fact]
        public void RoundTrip_ConsumerFields_AreNotWritten()
        {
            var holder = new Holder { WithTable = Value, StructsOnly = Value };

            var parsed = Holder.Serializer.Parse(Holder.Serializer.WriteToArray(holder));

            var expected = (Value.X, 0, 0L);
            (parsed.WithTable.Small.X, parsed.WithTable.Small.Extra, parsed.WithTable.Small.More).Should().Be(expected);
            (parsed.StructsOnly.Small.X, parsed.StructsOnly.Small.Extra, parsed.StructsOnly.Small.More)
                .Should()
                .Be(expected);
        }

        /// <summary>
        /// The union is no larger than its parts: the reference, the discriminator, and the largest struct at the
        /// schema alignment of the struct members.
        /// </summary>
        [Fact]
        public void Union_Size_IsNoLargerThanItsParts()
        {
            Unsafe.SizeOf<StructsOnly>().Should().Be(8 + Unsafe.SizeOf<Small>());
            Unsafe.SizeOf<WithTable>().Should().Be(8 + 8 + Unsafe.SizeOf<Small>());
        }
    }
}
