using System.Text.Json;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class JsonTests
{
    private const string Sample = """
        {
          "s": "text",
          "n": 42.5,
          "nInt": 7,
          "nStr": "12.25",
          "nExp": "1e3",
          "nBad": "abc",
          "nNaN": "NaN",
          "t": true,
          "f": false,
          "o": { "inner": 1 },
          "a": [1, 2],
          "nul": null,
          "iso": "2026-10-28T10:00:00Z",
          "isoOffset": "2026-10-28T15:50:00.123456+09:00",
          "isoNanos": "2026-10-28T10:00:00.123456789Z",
          "isoDate": "2026-11-01",
          "isoBad": "tomorrow",
          "epoch": 1761654321,
          "epochFrac": 1761654321.25,
          "epochStr": "1761654321",
          "epochHuge": 1e300,
          "epochMs": 1761654321000
        }
        """;

    private static JsonElement Root()
    {
        using JsonDocument doc = JsonDocument.Parse(Sample);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Str_ReturnsOnlyStrings()
    {
        JsonElement e = Root();

        Assert.Equal("text", Json.Str(e, "s"));
        Assert.Null(Json.Str(e, "n"));
        Assert.Null(Json.Str(e, "nul"));
        Assert.Null(Json.Str(e, "missing"));
    }

    [Fact]
    public void Num_AcceptsNumbersAndNumericStrings_RejectsEverythingElse()
    {
        JsonElement e = Root();

        Assert.Equal(42.5, Json.Num(e, "n"));
        Assert.Equal(7, Json.Num(e, "nInt"));
        Assert.Equal(12.25, Json.Num(e, "nStr"));
        Assert.Equal(1000, Json.Num(e, "nExp"));
        Assert.Null(Json.Num(e, "nBad"));
        Assert.Null(Json.Num(e, "nNaN"));
        Assert.Null(Json.Num(e, "t"));
        Assert.Null(Json.Num(e, "nul"));
        Assert.Null(Json.Num(e, "missing"));
    }

    [Fact]
    public void Bool_ReturnsOnlyJsonBooleans()
    {
        JsonElement e = Root();

        Assert.True(Json.Bool(e, "t"));
        Assert.False(Json.Bool(e, "f"));
        Assert.Null(Json.Bool(e, "s"));
        Assert.Null(Json.Bool(e, "missing"));
    }

    [Fact]
    public void ObjAndArr_CheckValueKind()
    {
        JsonElement e = Root();

        Assert.Equal(1, Json.Obj(e, "o")!.Value.GetProperty("inner").GetInt32());
        Assert.Null(Json.Obj(e, "a"));
        Assert.Null(Json.Obj(e, "nul"));
        Assert.Equal(2, Json.Arr(e, "a")!.Value.GetArrayLength());
        Assert.Null(Json.Arr(e, "o"));
        Assert.Null(Json.Arr(e, "missing"));
    }

    [Fact]
    public void Accessors_ReturnNull_WhenElementIsNotAnObject()
    {
        JsonElement array = Json.Arr(Root(), "a")!.Value;

        Assert.Null(Json.Str(array, "s"));
        Assert.Null(Json.Num(array, "n"));
        Assert.Null(Json.Bool(array, "t"));
        Assert.Null(Json.Obj(array, "o"));
        Assert.Null(Json.Arr(array, "a"));
        Assert.Null(Json.Iso(array, "iso"));
        Assert.Null(Json.EpochSeconds(array, "epoch"));
        Assert.Null(Json.Str(default, "s"));
    }

    [Fact]
    public void Iso_ParsesUtcOffsetsFractionsAndDateOnly()
    {
        JsonElement e = Root();

        Assert.Equal(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero), Json.Iso(e, "iso"));

        DateTimeOffset withOffset = Json.Iso(e, "isoOffset")!.Value;
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 15, 50, 0, 123, 456, TimeSpan.FromHours(9)), withOffset);
        Assert.Equal(TimeSpan.FromHours(9), withOffset.Offset);

        // 9 fractional digits are rounded to .NET's 100 ns ticks.
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_568), Json.Iso(e, "isoNanos"));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), Json.Iso(e, "isoDate"));
        Assert.Null(Json.Iso(e, "isoBad"));
        Assert.Null(Json.Iso(e, "epoch"));
    }

    [Fact]
    public void EpochSeconds_AcceptsIntegersFractionsAndStrings_RejectsOutOfRange()
    {
        JsonElement e = Root();

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761654321), Json.EpochSeconds(e, "epoch"));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1761654321250), Json.EpochSeconds(e, "epochFrac"));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761654321), Json.EpochSeconds(e, "epochStr"));
        Assert.Null(Json.EpochSeconds(e, "epochHuge"));
        Assert.Null(Json.EpochSeconds(e, "s"));
    }

    [Fact]
    public void EpochMilliseconds_ReadsMilliseconds()
    {
        JsonElement e = Root();

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761654321), Json.EpochMilliseconds(e, "epochMs"));
        Assert.Null(Json.EpochMilliseconds(e, "epochHuge"));
    }

    [Fact]
    public void SharedJsonLenient_AllowsCommentsAndTrailingCommas()
    {
        const string jsonc = """
            {
              // line comment
              "security": { "auth": { "selectedType": "oauth-personal", }, },
              /* block comment */
              "selectedAuthType": "gemini-api-key",
            }
            """;

        using JsonDocument doc = JsonDocument.Parse(jsonc, SharedJson.Lenient);

        JsonElement auth = Json.Obj(Json.Obj(doc.RootElement, "security")!.Value, "auth")!.Value;
        Assert.Equal("oauth-personal", Json.Str(auth, "selectedType"));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(jsonc).Dispose());
    }
}
