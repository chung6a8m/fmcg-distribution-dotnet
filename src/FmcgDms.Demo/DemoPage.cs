using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using FmcgDms.Api.Authentication;
using Microsoft.AspNetCore.Antiforgery;

namespace FmcgDms.Demo;

internal static class DemoPage
{
    private static readonly (string Key, string Label, int Max)[] Fields =
    [
        ("customerCode", "Mã khách hàng", 32), ("customerName", "Tên khách hàng", 120),
        ("shipToName", "Tên điểm giao", 120), ("address", "Địa chỉ giả lập", 180),
        ("skuCode", "Mã SKU", 32), ("skuName", "Tên hàng", 120), ("uom", "Đơn vị tính", 12),
        ("warehouseCode", "Mã kho", 32), ("vehicleCode", "Mã xe", 32),
        ("orderCode", "Mã đơn A", 32), ("tripCode", "Mã chuyến", 32)
    ];
    private static readonly string[] Measures =
        ["Tồn kho", "Tồn xe", "Đã bán", "Giá trị kho", "Giá trị xe", "Giá vốn", "Giữ A", "Quota B", "Công nợ A", "Công nợ B", "Tiền giữ hộ", "Quỹ"];
    internal static string MeasureLabel(int index) => Measures[index];
    private static readonly string[] Actions =
    [
        "Xếp 70 lên xe", "Thử bán B 31 vượt quota", "Ghi bán B 20 và thu 200", "Giao đơn A 25",
        "Đề nghị bàn giao 200", "Thủ quỹ xác nhận đã đếm 200", "Trả hàng và kết chuyến"
    ];

    public static DemoInputs Inputs(IFormCollection form)
    {
        var values = Fields.Select(field =>
        {
            string value = form[field.Key].ToString().Trim();
            return value.Length > 0 && value.Length <= field.Max && !value.Any(char.IsControl)
                ? value : throw new InvalidOperationException($"Kiểm tra lại {field.Label}.");
        }).ToArray();
        if (string.Equals(values[7], values[8], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mã kho và mã xe cần khác nhau.");
        if (!int.TryParse(form["opening"], out int qty) || qty is < 70 or > 100000 ||
            !int.TryParse(form["unitCost"], out int cost) || cost is < 1 or > 100000)
            throw new InvalidOperationException("Nhập tồn đầu kỳ từ 70 đến 100000 và giá vốn đơn vị từ 1 đến 100000.");
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6],
            values[7], values[8], values[9], values[10], qty, cost);
    }

    public static string Render(DemoManifest meta, ClaimsPrincipal user, AntiforgeryTokenSet token,
        DemoScene? scene, DemoCheckpoint[] points, dynamic[] sources, bool offered, string result,
        DemoBreakdown? breakdown)
    {
        string tenant = SyntheticAuth.GetTenantId(user);
        string role = user.FindFirst("synthetic_role")!.Value;
        string csrf = $"<input type=hidden name=\"{H(token.FormFieldName)}\" value=\"{H(token.RequestToken)}\">";
        string tenantField = $"<input type=hidden name=tenant value=\"{H(tenant)}\">";
        var page = new StringBuilder($"""
            <!doctype html><html lang="vi"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Demo chuyến bán hàng | FMCG DMS</title>
            <link rel="stylesheet" href="/tokens.css"><link rel="stylesheet" href="/demo.css">
            </head><body><main><header><h1>Demo chuyến bán hàng</h1>
            <p>Dữ liệu giả lập · {H(meta.Mode)} · {H(tenant)}</p>
            <form action="/identity" method="post">{csrf}<label>Vai trò đang dùng <select name="persona">
            """);
        foreach (string t in DemoRun.Tenants)
            foreach (string r in DemoScenario.Roles)
                page.Append($"<option value=\"{t}:{r}\" {(t == tenant && r == role ? "selected" : "")}>{t} · {RoleLabel(r)}</option>");
        page.Append("</select></label><button>Đổi vai trò</button></form></header>");
        if (!string.IsNullOrEmpty(result)) page.Append($"<p class=\"notice\" role=\"status\">{H(result)}</p>");
        if (scene is null || points.Length == 0)
        {
            page.Append("<section><h2>Tạo đơn và chuyến đầu tiên</h2><p>Mẫu nghiệp vụ dùng đơn A 40, quota B 30, giá bán 15 và khoản thu 200 (đơn vị giả lập).</p>");
            if (role == "warehouse")
            {
                page.Append($"<form class=\"setup\" action=\"/setup\" method=\"post\">{csrf}{tenantField}");
                foreach (var (key, label, max) in Fields)
                    page.Append($"<label>{label}<input name=\"{key}\" maxlength=\"{max}\" required></label>");
                page.Append("""
                    <label>Tồn đầu kỳ<input type="number" name="opening" min="70" max="100000" value="100" required></label>
                    <label>Giá vốn một đơn vị<input type="number" name="unitCost" min="1" max="100000" value="10" required></label>
                    <div><button>Tạo chuyến</button></div></form>
                    """);
            }
            else page.Append("<p>Chọn vai trò Kho để tạo chuyến.</p>");
            page.Append("</section>");
        }
        else
        {
            page.Append($"<section><h2>Chuyến {H(scene.Inputs.TripCode)}</h2><p>{H(scene.Inputs.CustomerName)} · {H(scene.Inputs.SkuName)} · {H(scene.Inputs.WarehouseCode)} / {H(scene.Inputs.VehicleCode)}</p><ol class=\"actions\">");
            int current = points.Length - 1;
            for (int action = 1; action <= Actions.Length; action++)
            {
                bool done = action == 5 ? offered : current >= (action < 5 ? action : action - 1);
                bool available = done || (action == 6 ? current >= 4 && offered : current >= (action <= 5 ? action - 1 : action - 2));
                string neededRole = DemoScenario.Role(action);
                page.Append($"<li><span>{Actions[action - 1]}</span>");
                if (available && role == neededRole)
                    page.Append($"<form action=\"/step\" method=\"post\">{csrf}{tenantField}<button name=\"action\" value=\"{action}\">{(done ? "Gửi lại lệnh" : "Thực hiện")}</button></form>");
                else page.Append($"<small>{(done ? "Đã ghi" : available ? RoleLabel(neededRole) : "Chờ bước trước")}</small>");
                page.Append("</li>");
            }
            page.Append("</ol></section><section><h2>Đối soát theo từng bước</h2><p>Số đọc từ SQL / số kỳ vọng AC-03. Bấm số đọc từ SQL để xem các dòng cấu thành tại mốc đó.</p>");
            page.Append("<div class=\"table\" tabindex=\"0\" role=\"region\" aria-label=\"Bảng đối soát\"><table><thead><tr><th scope=\"col\">Bước</th>");
            foreach (string measure in Measures) page.Append($"<th scope=\"col\">{measure}</th>");
            page.Append("</tr></thead><tbody>");
            foreach (var point in points)
            {
                page.Append($"<tr><th scope=\"row\">{H(point.Step)}</th>");
                for (int column = 0; column < point.Cells.Length; column++)
                {
                    var cell = point.Cells[column];
                    string label = $"{Measures[column]} {F(cell.Actual)} tại {point.Step}";
                    string href = $"/?moc={Uri.EscapeDataString(point.Step)}&chiSo={Uri.EscapeDataString(cell.Field)}#chi-tiet-doi-soat";
                    page.Append($"<td id=\"{CellId(point.Step, cell.Field)}\">{(cell.Difference is not (null or 0m) ? "⚠ " : "")}<a href=\"{H(href)}\" aria-label=\"{H(label)}\">{F(cell.Actual)}</a>{(cell.Expected is decimal expected ? " / " + F(expected) : "")}</td>");
                }
                page.Append("</tr>");
            }
            page.Append("</tbody></table></div><p><a href=\"/report.xlsx\">Xuất Excel</a> · <a href=\"/report.json\">Báo cáo JSON</a></p></section>");
            if (breakdown is not null)
            {
                var point = points.Single(p => p.Step == breakdown.Step);
                RenderBreakdown(page, breakdown, Measures[Array.IndexOf(point.Cells, breakdown.Cell)]);
            }
            page.Append("<section><h2>Chứng từ và dòng sổ</h2><div class=\"table\" tabindex=\"0\" role=\"region\" aria-label=\"Chứng từ và dòng sổ\"><table><thead><tr><th>Chứng từ</th><th>Loại</th><th>Sổ</th><th>Số lượng</th><th>Giá trị</th></tr></thead><tbody>");
            foreach (IDictionary<string, object> row in sources)
            {
                page.Append($"<tr><td><details><summary>{H(row["SourceNumber"]?.ToString())}</summary>");
                page.Append($"<small>Nguồn {H(row["SourceId"]?.ToString())}<br>Dòng {H(row["LineId"]?.ToString())}<br>Bút toán {H(row["EffectId"]?.ToString())}</small></details></td>");
                page.Append($"<td>{H(row["SourceKind"]?.ToString())}</td><td>{H(row["LedgerKind"]?.ToString())}</td><td>{H(row["QuantityDelta"]?.ToString())}</td><td>{H(row["AmountDelta"]?.ToString())}</td></tr>");
            }
            page.Append("</tbody></table></div></section>");
        }
        page.Append($"<footer>RunId <code>{H(meta.RunId)}</code> · Seed {H(meta.SeedVersion)} · Source <code>{H(meta.Source)}</code></footer></main></body></html>");
        return page.ToString();
    }

    private static string CellId(string step, string field) => H($"doi-soat-{step}-{field}");

    private static void RenderBreakdown(StringBuilder page, DemoBreakdown breakdown, string measure)
    {
        decimal total = breakdown.Rows.Sum(row => row.Value);
        page.Append($"""
            <section id="chi-tiet-doi-soat" tabindex="-1" aria-labelledby="tieu-de-chi-tiet">
            <p><a href="/#{CellId(breakdown.Step, breakdown.Cell.Field)}">Về ô đối soát</a></p>
            <h2 id="tieu-de-chi-tiet">{H(measure)} {F(breakdown.Cell.Actual)} · {H(breakdown.Step)}</h2>
            <p>Tổng các dòng cấu thành <strong>{F(total)}</strong></p>
            """);
        if (total != breakdown.Cell.Actual)
            page.Append($"<p class=\"notice\" role=\"status\">Chi tiết lệch {F(total - breakdown.Cell.Actual)} so với số đã lưu tại mốc này.</p>");
        if (breakdown.Rows.Count == 0)
            page.Append("<p>Tại mốc này chưa có dòng cấu thành chỉ số.</p>");
        else
        {
            page.Append("""
                <div class="table" tabindex="0" role="region" aria-label="Các dòng cấu thành"><table>
                <thead><tr><th scope="col">Chứng từ / nguồn</th><th scope="col">Sổ</th><th scope="col">Phần đóng góp</th></tr></thead><tbody>
                """);
            foreach (var row in breakdown.Rows)
            {
                page.Append("<tr><td>");
                if (row.Line is { } line)
                    page.Append($"<details><summary>{H(row.Label)}</summary><small>Loại {H(line.SourceKind)}<br>Nguồn {line.SourceId}<br>Dòng {line.LineId}<br>Bút toán {line.EffectId}</small></details>");
                else page.Append(H(row.Label));
                page.Append($"</td><td>{H(row.Kind)}</td><td>{F(row.Value)}</td></tr>");
            }
            page.Append("</tbody></table></div>");
        }
        page.Append("</section>");
    }

    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string F(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string RoleLabel(string role) => role switch
    {
        "warehouse" => "Kho", "sales" => "Bán hàng", "cashier" => "Thủ quỹ", _ => "Đối soát"
    };

    public const string Css = """
        @font-face{font-family:"Noto Sans";src:url("/font/400");font-weight:400;font-display:swap}
        @font-face{font-family:"Noto Sans";src:url("/font/700");font-weight:700;font-display:swap}
        *{box-sizing:border-box}
        body{margin:0;font:var(--fmcg-font-size-body)/1.5 var(--fmcg-font-family);background:var(--fmcg-color-surface-page);color:var(--fmcg-color-text-primary)}
        main{max-width:1200px;margin:auto;padding:1rem}
        header,section{padding:1rem;margin-bottom:1rem;border:1px solid var(--fmcg-color-border-subtle);border-radius:8px;background:var(--fmcg-color-surface-card)}
        h1,h2{margin:0 0 .5rem} p{margin:.5rem 0 1rem}
        form{display:flex;gap:.5rem;align-items:end;flex-wrap:wrap}
        label{display:grid;gap:.25rem}
        input,button,select{font:inherit;min-height:36px;padding:.35rem .6rem;border:1px solid var(--fmcg-color-border-control);border-radius:4px;background:var(--fmcg-color-surface-card);color:inherit}
        button{background:var(--fmcg-color-action-primary-background);color:var(--fmcg-color-action-primary-foreground);cursor:pointer}
        :focus-visible{outline:2px solid var(--fmcg-color-focus-ring);outline-offset:2px}
        a{color:var(--fmcg-color-link-text)} .notice{background:var(--fmcg-color-status-info-background);padding:.75rem}
        .setup{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,15rem),1fr));gap:.8rem}
        .actions{padding-inline-start:1.5rem} .actions li{margin-bottom:.45rem}
        .actions form,.actions small{display:inline-block;margin-inline-start:.7rem}
        .table{overflow:auto} table{width:100%;border-collapse:collapse;text-align:left;font-size:var(--fmcg-font-size-table)}
        th,td{padding:.45rem;vertical-align:top;border-bottom:1px solid var(--fmcg-color-border-subtle)}
        thead th{background:var(--fmcg-color-surface-subtle)}
        td{white-space:nowrap} tr:nth-child(even){background:var(--fmcg-color-surface-row-alternate)}
        footer{overflow-wrap:anywhere;color:var(--fmcg-color-text-secondary)}
        @media(max-width:767px){input,button,select{min-height:48px}.actions form,.actions small{display:block;margin:.25rem 0}}
        """;
}
