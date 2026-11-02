using System.Text;

namespace Fern.Site;

/// <summary>
/// The exact JSON bytes Go's <c>encoding/json</c> writes for the two Site
/// records whose bytes are hashed or compared.
///
/// This is not a general encoder and must not become one. It exists because two
/// facts about the old owner are identity-bearing and neither is expressible
/// with System.Text.Json's defaults:
///
/// <list type="number">
/// <item>Field order is the Go struct's declaration order, not alphabetical.</item>
/// <item>Two different escaping modes are in play. The instructions digest goes
/// through an encoder with <c>SetEscapeHTML(false)</c>, so <c>&lt;</c>,
/// <c>&gt;</c> and <c>&amp;</c> stay raw; the published return report goes
/// through plain <c>json.Marshal</c>, whose default escapes each of them as a
/// six-character <c>\u00xx</c> form. Getting this backwards changes a digest
/// while leaving every ASCII test green.</item>
/// </list>
///
/// Proved by the escaping probe in the round-trip golden, whose preimage carries
/// a quote, a backslash, the three HTML-significant characters, a tab, U+0001, a
/// newline, a carriage return, U+00E9, U+4E2D, U+2028, U+2029 and U+007F.
/// </summary>
static class GoJson
{
    /// <summary>Go escapes these two code points inside strings even when HTML escaping is off, because they break JavaScript.</summary>
    const char LineSeparator = '\u2028';
    const char ParagraphSeparator = '\u2029';

    /// <summary>
    /// The instructions as the digest sees them: declaration order, and HTML
    /// escaping OFF, because the digest comes from an encoder configured that way.
    /// </summary>
    internal static byte[] InstructionsBytes(AttemptInstructions instructions, bool escapeHtml)
    {
        var output = new StringBuilder();
        output.Append('{');
        WriteMember(output, "schema", instructions.Schema, escapeHtml, first: true);
        WriteMember(output, "matrix_id", instructions.MatrixId, escapeHtml, first: false);
        WriteMember(output, "coordinate_id", instructions.CoordinateId, escapeHtml, first: false);
        WriteMember(output, "attempt_id", instructions.AttemptId, escapeHtml, first: false);
        WriteMember(output, "run_hash", instructions.RunHash, escapeHtml, first: false);
        WriteMember(output, "execution_profile_revision", instructions.ExecutionProfileRevision, escapeHtml, first: false);
        WriteMember(output, "worker_request_content_digest", instructions.WorkerRequestContentDigest, escapeHtml, first: false);
        output.Append(',');
        WriteString("files", output, escapeHtml);
        output.Append(':');
        WriteFiles(output, instructions.Files, escapeHtml);
        output.Append('}');
        return Utf8(output);
    }

    /// <summary>
    /// The return report as it is written to disk: declaration order, and HTML
    /// escaping ON, because Go publishes it with plain <c>json.Marshal</c>.
    /// </summary>
    internal static byte[] ReturnReportBytes(AttemptReturnReport report, bool escapeHtml)
    {
        var output = new StringBuilder();
        output.Append('{');
        WriteMember(output, "schema", report.Schema, escapeHtml, first: true);
        WriteMember(output, "instructions_digest", report.InstructionsDigest, escapeHtml, first: false);
        WriteMember(output, "attempt_id", report.AttemptId, escapeHtml, first: false);
        WriteMember(output, "worker_result_content_digest", report.RunnerReportContentDigest, escapeHtml, first: false);
        output.Append(',');
        WriteString("files", output, escapeHtml);
        output.Append(':');
        WriteFiles(output, report.Files, escapeHtml);
        output.Append('}');
        return Utf8(output);
    }

    static void WriteFiles(StringBuilder output, IReadOnlyList<AttemptFile> files, bool escapeHtml)
    {
        // Go marshals a nil slice as null and an empty one as []. Every caller
        // here builds a slice, so [] is the empty form; null never occurs and is
        // deliberately not representable.
        output.Append('[');
        for (var index = 0; index < files.Count; index++)
        {
            if (index > 0)
            {
                output.Append(',');
            }
            output.Append('{');
            WriteMember(output, "relative_path", files[index].RelativePath, escapeHtml, first: true);
            output.Append(",\"byte_count\":");
            output.Append(files[index].ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WriteMember(output, "byte_digest", files[index].ByteDigest, escapeHtml, first: false);
            output.Append('}');
        }
        output.Append(']');
    }

    /// <summary>
    /// One JSON string with quotes, for a caller that assembles its own
    /// document -- the CLI's indented execution report. The escaping rules are
    /// the ones documented on this class; nothing else here is public, because
    /// nothing else here is a general encoder.
    /// </summary>
    internal static string QuotedString(string value, bool escapeHtml)
    {
        var output = new StringBuilder();
        WriteString(value, output, escapeHtml);
        return output.ToString();
    }

    static void WriteMember(StringBuilder output, string name, string value, bool escapeHtml, bool first)
    {
        if (!first)
        {
            output.Append(',');
        }
        WriteString(name, output, escapeHtml);
        output.Append(':');
        WriteString(value, output, escapeHtml);
    }

    /// <summary>
    /// One JSON string, quotes included, byte for byte as Go writes it: the two
    /// structural characters, the four short control forms, <c>\u00xx</c> for
    /// every other C0 control, the three HTML characters only when asked, and
    /// the two JavaScript line terminators always. Everything else, including
    /// U+007F and all non-ASCII, is written raw.
    /// </summary>
    static void WriteString(string value, StringBuilder output, bool escapeHtml)
    {
        output.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"':
                    output.Append("\\\"");
                    continue;
                case '\\':
                    output.Append("\\\\");
                    continue;
                case '\n':
                    output.Append("\\n");
                    continue;
                case '\r':
                    output.Append("\\r");
                    continue;
                case '\t':
                    output.Append("\\t");
                    continue;
                case '<' or '>' or '&' when escapeHtml:
                    AppendCodeUnit(output, character);
                    continue;
                case LineSeparator or ParagraphSeparator:
                    AppendCodeUnit(output, character);
                    continue;
            }
            if (character < 0x20)
            {
                AppendCodeUnit(output, character);
                continue;
            }
            if (char.IsSurrogate(character))
            {
                // Go replaces invalid UTF-8 with U+FFFD. A lone surrogate is the
                // C# shape of the same defect, and a well-formed pair is copied.
                var pairIsWellFormed = char.IsHighSurrogate(character)
                    && index + 1 < value.Length
                    && char.IsLowSurrogate(value[index + 1]);
                if (pairIsWellFormed)
                {
                    output.Append(character).Append(value[index + 1]);
                    index++;
                }
                else
                {
                    output.Append('�');
                }
                continue;
            }
            output.Append(character);
        }
        output.Append('"');
    }

    static void AppendCodeUnit(StringBuilder output, char character) =>
        output.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));

    static byte[] Utf8(StringBuilder output) => new UTF8Encoding(false, false).GetBytes(output.ToString());
}
