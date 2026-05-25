public class FlatBufferBuilderTests
{
    [Test]
    public async Task EndTable_grows_a_nearly_full_buffer()
    {
        // A 1-byte initial buffer leaves no room for the vtable, so EndTable must grow the buffer.
        // Before EndTable ensured capacity, this overran the buffer and threw.
        var builder = new FlatBufferBuilder(1);
        builder.StartTable(1);
        builder.AddByte(0, 7, 0);
        var bytes = builder.FinishSizePrefixed(builder.EndTable());
        await Assert.That(bytes.Length > 0).IsTrue();
    }
}
