using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class DemoExcel
{
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Package = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string Relationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    public static async Task<byte[]> Create(SqlConnection db, DemoManifest metadata, string source,
        string tenant, DemoScene scene, DemoCheckpoint[] points)
    {
        var summary = new List<object?[]>
        {
            new object?[] { "Mốc", "Chỉ số", "Thực tế SQL", "Kỳ vọng AC-03", "Lệch kỳ vọng",
                "Tổng dòng cấu thành", "Lệch chi tiết", "Số dòng", "Kết quả lệnh", "Quan sát UTC" }
        };
        var detail = new List<object?[]>
        {
            new object?[] { "Mốc", "Chỉ số", "Chứng từ / nguồn", "Sổ", "Phần đóng góp", "Loại chứng từ",
                "ID nguồn", "ID dòng", "ID bút toán", "Lượng ghi sổ", "Giá trị ghi sổ" }
        };
        var links = new List<XElement>();
        foreach (var point in points)
        {
            for (int index = 0; index < point.Cells.Length; index++)
            {
                var cell = point.Cells[index];
                string measure = DemoPage.MeasureLabel(index);
                var breakdown = await DemoScenario.Breakdown(db, tenant, scene, points, point.Step, cell.Field)
                    ?? throw new InvalidOperationException("Checkpoint disappeared during export.");
                decimal total = breakdown.Rows.Sum(row => row.Value);
                summary.Add([point.Step, measure, cell.Actual, cell.Expected, cell.Difference, total,
                    total - cell.Actual, breakdown.Rows.Count, point.Outcome, point.ObservedAt]);
                links.Add(new(Sheet + "hyperlink", new XAttribute("ref", $"C{summary.Count}"),
                    new XAttribute("location", $"'Dòng cấu thành'!A{detail.Count + 1}")));
                if (breakdown.Rows.Count == 0)
                    detail.Add([point.Step, measure, "Chưa phát sinh dòng", null, null]);
                foreach (var row in breakdown.Rows)
                {
                    var line = row.Line;
                    detail.Add([point.Step, measure, row.Label, row.Kind, row.Value, line?.SourceKind,
                        line?.SourceId.ToString(), line?.LineId.ToString(), line?.EffectId.ToString(),
                        line?.Quantity, line?.Amount]);
                }
            }
        }

        var journal = new List<object?[]>
        {
            new object?[] { "Mốc ghi nhận đầu tiên", "Chứng từ", "Loại chứng từ", "Sổ", "Lượng ghi sổ",
                "Giá trị ghi sổ", "ID nguồn", "ID dòng", "ID bút toán", "Ghi nhận UTC" }
        };
        foreach (IDictionary<string, object?> row in await DemoScenario.Sources(db, scene))
        {
            var recorded = new DateTimeOffset((DateTime)row["RecordedAtUtc"]!, TimeSpan.Zero);
            var first = points.FirstOrDefault(point => point.ObservedAt >= recorded);
            if (first is null) continue;
            journal.Add([first.Step, row["SourceNumber"], row["SourceKind"], row["LedgerKind"], row["QuantityDelta"],
                row["AmountDelta"], row["SourceId"]?.ToString(), row["LineId"]?.ToString(), row["EffectId"]?.ToString(), recorded]);
        }

        object?[][] provenance =
        [
            ["Thông tin", "Giá trị"],
            ["Môi trường", "SYNTHETIC_NONPRODUCTION"],
            ["Phạm vi bằng chứng", "Đối soát AC-03 chạy trên Windows LocalDB"],
            ["Trạng thái PREPROD vật lý", "NOT_VERIFIED"],
            ["RunId", metadata.RunId], ["Tenant", tenant], ["Mã chuyến", scene.Inputs.TripCode],
            ["Chế độ", metadata.Mode], ["Seed", metadata.SeedVersion], ["Oracle SHA-256", metadata.OracleSha256],
            ["Source tạo phiên", metadata.Source], ["Source xuất Excel", source],
            ["Tạo phiên UTC", metadata.CreatedAt], ["Xuất UTC", DateTimeOffset.UtcNow],
            ["Đơn vị", "SYNTHETIC_UNITLESS"],
            ["Cách tra", "Bấm ô Thực tế SQL để sang nhóm dòng cấu thành tương ứng."],
            ["Dòng cấu thành", "Lũy kế tại mốc. Lọc theo mốc và chỉ số trước khi cộng."],
            ["Chứng từ phát sinh", "Mỗi dòng chỉ hiện tại mốc quan sát đầu tiên, kể cả chứng từ chưa ghi sổ."]
        ];
        string[] names = ["Đối soát", "Dòng cấu thành", "Chứng từ phát sinh", "Nguồn bằng chứng"];
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var sheets = Enumerable.Range(1, names.Length).ToArray();
            Write(zip, "[Content_Types].xml", new XElement(Types + "Types",
                new XElement(Types + "Default", new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(Types + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                Part("/xl/workbook.xml", "sheet.main"), Part("/xl/styles.xml", "styles"),
                sheets.Select(index => Part($"/xl/worksheets/sheet{index}.xml", "worksheet"))));
            Write(zip, "_rels/.rels", new XElement(Package + "Relationships",
                Link("rId1", "officeDocument", "xl/workbook.xml")));
            Write(zip, "xl/_rels/workbook.xml.rels", new XElement(Package + "Relationships",
                sheets.Select(index => Link($"rId{index}", "worksheet", $"worksheets/sheet{index}.xml")),
                Link("styles", "styles", "styles.xml")));
            Write(zip, "xl/workbook.xml", new XElement(Sheet + "workbook", new XAttribute(XNamespace.Xmlns + "r", Office),
                new XElement(Sheet + "bookViews", new XElement(Sheet + "workbookView")),
                new XElement(Sheet + "sheets", sheets.Select(index => new XElement(Sheet + "sheet",
                    new XAttribute("name", names[index - 1]), new XAttribute("sheetId", index),
                    new XAttribute(Office + "id", $"rId{index}"))))));
            Write(zip, "xl/styles.xml", XElement.Parse(Styles));
            Write(zip, "xl/worksheets/sheet1.xml", Worksheet(summary, [20, 21, 16, 17, 17, 24, 17, 12, 35, 25], links));
            Write(zip, "xl/worksheets/sheet2.xml", Worksheet(detail, [20, 21, 30, 27, 18, 28, 39, 39, 39, 16, 17]));
            Write(zip, "xl/worksheets/sheet3.xml", Worksheet(journal, [24, 20, 28, 27, 17, 17, 39, 39, 39, 25]));
            Write(zip, "xl/worksheets/sheet4.xml", Worksheet(provenance, [29, 90]));
        }
        return buffer.ToArray();
    }

    private static XElement Part(string name, string type) => new(Types + "Override",
        new XAttribute("PartName", name), new XAttribute("ContentType", $"application/vnd.openxmlformats-officedocument.spreadsheetml.{type}+xml"));

    private static XElement Link(string id, string type, string target) => new(Package + "Relationship",
        new XAttribute("Id", id), new XAttribute("Type", Relationship + type), new XAttribute("Target", target));

    private static XElement Worksheet(IReadOnlyList<object?[]> rows, int[] widths, List<XElement>? links = null)
    {
        var result = new XElement(Sheet + "worksheet",
            new XElement(Sheet + "sheetViews", new XElement(Sheet + "sheetView", new XAttribute("workbookViewId", 0),
                new XElement(Sheet + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"),
                    new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
            new XElement(Sheet + "cols", widths.Select((width, i) => new XElement(Sheet + "col",
                new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", width), new XAttribute("customWidth", 1)))),
            new XElement(Sheet + "sheetData", rows.Select((row, i) => new XElement(Sheet + "row",
                new XAttribute("r", i + 1), row.Select((value, j) => Cell(value, i, j, links is not null))))),
            new XElement(Sheet + "autoFilter", new XAttribute("ref", $"A1:{(char)('A' + widths.Length - 1)}{rows.Count}")));
        if (links is { Count: > 0 }) result.Add(new XElement(Sheet + "hyperlinks", links));
        return result;
    }

    private static XElement? Cell(object? value, int row, int column, bool linked)
    {
        if (value is null) return null;
        var cell = new XElement(Sheet + "c", new XAttribute("r", $"{(char)('A' + column)}{row + 1}"));
        if (row == 0) cell.Add(new XAttribute("s", 1));
        switch (value)
        {
            case DateTimeOffset date:
                cell.Add(new XAttribute("s", 3), new XElement(Sheet + "v", date.UtcDateTime.ToOADate()));
                break;
            case decimal number:
                bool whole = decimal.Truncate(number) == number;
                int style = linked && column == 2 ? (whole ? 6 : 4) : (whole ? 5 : 2);
                cell.Add(new XAttribute("s", style), new XElement(Sheet + "v", number));
                break;
            case int number:
                cell.Add(new XElement(Sheet + "v", number));
                break;
            default:
                cell.Add(new XAttribute("t", "inlineStr"), new XElement(Sheet + "is", new XElement(Sheet + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"), Convert.ToString(value, CultureInfo.InvariantCulture))));
                break;
        }
        return cell;
    }

    private static void Write(ZipArchive zip, string path, XElement content)
    {
        using var stream = zip.CreateEntry(path).Open();
        new XDocument(content).Save(stream);
    }

    private const string Styles = """
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="2"><numFmt numFmtId="164" formatCode="#,##0.####;[Red]-#,##0.####;0"/><numFmt numFmtId="165" formatCode="yyyy-mm-dd hh:mm:ss.000"/></numFmts>
          <fonts count="3">
            <font><sz val="11"/><name val="Calibri"/></font>
            <font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font>
            <font><u/><sz val="11"/><color rgb="FF315DA8"/><name val="Calibri"/></font>
          </fonts>
          <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF172B4D"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="7">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="164" fontId="2" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/>
            <xf numFmtId="3" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="3" fontId="2" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;
}
