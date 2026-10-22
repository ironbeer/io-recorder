using System.Globalization;
using System.Net;
using System.Text;

namespace IoRecorder.Core;

// The 「計測したPCと環境」 part at the top of the HTML report (environment.json): a few lines with what matters most for reading
// the numbers — the PC, the storage behind the drives this record used, what sits in every file request — and the rest of the
// machine under 「詳しく見る」. Older records without the file get nothing.
public static class EnvironmentReport
{
    private static readonly System.Text.RegularExpressions.Regex FilterLine = new(@"^\s*\S+\s+\d+\s+\d+(\.\d+)?\s+\d+\s*$");
    public static string Html(string session, AnalysisResult a, SessionManifest m)
    {
        string file = Path.Combine(session, "environment.json");
        if (!File.Exists(file)) return "";
        EnvironmentInfo env;
        try { env = JsonStore.Read<EnvironmentInfo>(file); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { return ""; }
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
        static string B(long? bytes) => bytes is not { } b ? "—" : b >= 1L << 40 ? $"{b / 1099511627776.0:N1} TiB" : b >= 1L << 30 ? $"{b / 1073741824.0:N1} GiB" : b >= 1L << 20 ? $"{b / 1048576.0:N0} MiB" : $"{b / 1024.0:N0} KiB";
        static string Yes(bool? value) => value switch { true => "あり", false => "なし", null => "不明" };
        static string On(bool? value) => value switch { true => "オン", false => "オフ", null => "不明" };

        // The PC.
        var pc = new List<string>();
        if (env.Computer is { } c) pc.Add($"{c.Manufacturer} {c.Model}".Trim() + (c.VirtualMachine ? "（仮想マシン）" : ""));
        pc.Add($"{env.OsName} {env.OsDisplayVersion}（ビルド {env.OsBuild}）");
        var cpu = env.Cpu;
        pc.Add($"CPU {cpu?.Name ?? env.CpuModel}" + (cpu?.Cores is { } cores ? $"（{cores}コア・{cpu.LogicalProcessors ?? env.LogicalProcessors}スレッド）" : $"（{env.LogicalProcessors}スレッド）"));
        int? speed = env.MemoryModules.Select(x => x.ConfiguredSpeedMHz is > 0 ? x.ConfiguredSpeedMHz : x.SpeedMHz).Where(s => s is > 0).Max();
        pc.Add($"メモリ {B((long)env.TotalPhysicalBytes)}" + (env.MemoryModules.Count > 0 ? $"（{env.MemoryModules.Count}枚" + (speed is { } mhz ? $"・{mhz} MHz" : "") + "）" : ""));
        if (env.Gpus.Count > 0) pc.Add("GPU " + string.Join("、", env.Gpus.Select(g => g.Name)));
        if (env.Power is { } power) pc.Add($"電源プラン {power.Plan ?? "不明"}・" + (power.OnAc switch { true => "AC電源", false => $"バッテリー{(power.BatteryPercent is { } p ? $" {p}%" : "")}", null => "電源不明" }));
        if (env.BootTimeUtc is { } boot) pc.Add($"記録の開始時点でOSの起動から {(m.StartedUtc - boot).TotalHours:N1} 時間");

        // The storage this record used: the drives of its files and the folders it was given, then where it was saved.
        var used = new HashSet<string>(a.Files.Select(f => PathPolicy.VolumeOf(f.Path)).Where(v => v.Length > 0), StringComparer.OrdinalIgnoreCase);
        static string Key(VolumeInfo v) => PathPolicy.VolumeOf(v.Root ?? v.Path);
        var volumes = env.Volumes.Where(v => v.Role != "ドライブ" || used.Contains(Key(v))).ToList();
        var storage = new List<string>();
        // One line per drive, with every role it has (the folders given and where the record was saved are often on one drive).
        foreach (var group in volumes.GroupBy(Key, StringComparer.OrdinalIgnoreCase))
        {
            var v = group.First();
            string roles = string.Join("・", group.Select(x => x.Role == "ドライブ" ? "この記録が使ったドライブ" : $"{x.Role} {x.Path}"));
            var line = new StringBuilder($"{E(v.Root ?? v.Path)}（{E(roles)}）: {E(v.FileSystem ?? "—")}");
            if (v.ClusterBytes is { } cluster) line.Append($"・クラスタ {B(cluster)}");
            if (v.BitLocker is { } bitlocker) line.Append($"・BitLocker {E(bitlocker)}");
            if (v.TotalBytes is { } total) line.Append($"・空き {B(v.FreeBytes)} / {B(total)}");
            foreach (var d in env.Disks.Where(d => v.DiskNumbers.Contains(d.Number)))
                line.Append($" → ディスク{d.Number} {E(d.Name)}（{E(d.MediaType)}・{E(d.BusType)}{(d.Health is { } h && h != "正常" ? $"・状態 {E(h)}" : "")}{(d.DynamicDisk ? "・ダイナミックディスク" : "")}）");
            if ((v.UncPath ?? v.Root) is { } unc && unc.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = unc.TrimStart('\\').Split('\\');
                foreach (var s in env.SmbConnections.Where(s => parts.Length >= 2 && string.Equals(s.Server, parts[0], StringComparison.OrdinalIgnoreCase) && string.Equals(s.Share, parts[1], StringComparison.OrdinalIgnoreCase)))
                    line.Append($" → SMB {E(s.Dialect)}・署名 {Yes(s.Signed)}・暗号化 {Yes(s.Encrypted)}・マルチチャネル {(env.SmbChannels.Count(ch => string.Equals(ch.Server, s.Server, StringComparison.OrdinalIgnoreCase)) is > 1 and var n ? $"{n}本" : "なし")}");
            }
            storage.Add(line.ToString());
        }
        var raid = env.StorageControllers.Where(x => x.Name?.Contains("RAID", StringComparison.OrdinalIgnoreCase) == true || x.Name?.Contains("RST", StringComparison.Ordinal) == true).Select(x => x.Name).ToList();
        if (env.Disks.Any(d => d.BusType == "RAID") || raid.Count > 0)
            storage.Add("RAID: " + E(string.Join("、", raid.DefaultIfEmpty("RAIDのコントローラーにつながったディスクあり"))) + "（RAIDのレベルやキャッシュの設定は、カードの中の情報なので読めません）");
        foreach (var s in env.StorageSpaces) storage.Add($"記憶域スペース {E(s.Name)}: {E(s.Resiliency)}・列 {s.Columns}・ストライプ {B(s.InterleaveBytes)}・{B(s.SizeBytes)}・{E(s.Health)}");

        // What every file request passes through.
        var around = new List<string>();
        if (env.Antivirus.Count > 0) around.Add("ウイルス対策 " + E(string.Join("、", env.Antivirus.Select(x => $"{x.Name}（{On(x.Enabled)}）"))));
        if (env.Defender is { } defender) around.Add($"Microsoft Defenderのリアルタイム保護 {On(defender.RealTimeProtection)}" + (defender.RunningMode is { } mode && mode != "Normal" ? $"（{E(mode)}）" : ""));
        foreach (var s in env.Services)
            around.Add($"{(s.Name == "SysMain" ? "SysMain（プリフェッチ）" : E(s.DisplayName ?? s.Name))} {E(s.State switch { "Running" => "動作中", "Stopped" => "停止", var other => other })}"
                + (s.StartMode == "Disabled" ? "（無効）" : ""));
        // A filter's line ends with its altitude and frame ("WdFilter   11   328010   0"); headers and error messages do not.
        var filters = env.FilterDrivers.Where(l => FilterLine.IsMatch(l)).Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).ToList();
        if (filters.Count > 0) around.Add($"フィルタードライバー {filters.Count} 件（{E(string.Join("、", filters))}）");

        string Row(params string?[] cells) => "<tr>" + string.Concat(cells.Select(x => $"<td>{E(x)}</td>")) + "</tr>";
        string Table(string title, string[] heads, IEnumerable<string> rows)
        {
            var list = rows.ToList();
            return list.Count == 0 ? "" : $"<h3>{E(title)}</h3><table><tr>{string.Concat(heads.Select(h => $"<th>{E(h)}</th>"))}</tr>{string.Concat(list)}</table>";
        }
        var details = new StringBuilder();
        details.Append(Table("ディスク", ["番号", "名前", "種類", "つなぎ方", "容量", "ファームウェア", "状態", "温度・摩耗", "ドライブ"],
            env.Disks.Select(d => Row(d.Number.ToString(CultureInfo.InvariantCulture), d.Name, d.MediaType, d.BusType, B(d.SizeBytes), d.Firmware, d.Health,
                d.TemperatureC is null && d.WearPercent is null ? "—" : $"{d.TemperatureC?.ToString() ?? "—"} °C・{d.WearPercent?.ToString() ?? "—"}%", string.Join(" ", d.DriveLetters)))));
        details.Append(Table("ドライブ", ["ドライブ", "役割", "ファイルシステム", "クラスタ", "BitLocker", "空き / 容量", "ディスク"],
            env.Volumes.Select(v => Row(v.Root ?? v.Path, v.Role, v.FileSystem, B(v.ClusterBytes), v.BitLocker ?? "—", $"{B(v.FreeBytes)} / {B(v.TotalBytes)}", string.Join(" ", v.DiskNumbers)))));
        details.Append(Table("ストレージのコントローラー", ["名前", "メーカー", "ドライバー", "日付"], env.StorageControllers.Select(x => Row(x.Name, x.Manufacturer, x.DriverVersion, x.DriverDate))));
        details.Append(Table("記憶域スペース", ["名前", "プール", "方式", "列", "ストライプ", "容量", "状態"], env.StorageSpaces.Select(s => Row(s.Name, s.Pool, s.Resiliency, s.Columns?.ToString(), B(s.InterleaveBytes), B(s.SizeBytes), s.Health))));
        details.Append(Table("SMBの接続", ["サーバー", "共有", "バージョン", "署名", "暗号化", "開いているファイル"], env.SmbConnections.Select(s => Row(s.Server, s.Share, s.Dialect, Yes(s.Signed), Yes(s.Encrypted), s.OpenFiles?.ToString()))));
        details.Append(Table("SMBのマルチチャネル", ["サーバー", "使用中", "PC側の速さ", "RSS", "RDMA"], env.SmbChannels.Select(s => Row(s.Server, Yes(s.Selected), s.ClientLinkSpeedBitsPerSecond is { } bps ? $"{bps / 1e9:N1} Gbps" : "—", Yes(s.ClientRss), Yes(s.ClientRdma)))));
        details.Append(Table("ネットワークドライブの割り当て", ["ドライブ", "共有"], env.MappedDrives.Select(p => Row(p.Key, p.Value))));
        details.Append(Table("ネットワークアダプター", ["名前", "説明", "種類", "速さ", "状態"], env.NetworkAdapters.Select(n => Row(n.Name, n.Description, n.Type, n.SpeedBitsPerSecond > 0 ? $"{n.SpeedBitsPerSecond / 1e9:N1} Gbps" : "—", n.Status))));
        details.Append(Table("メモリ", ["容量", "速さ", "メーカー", "型番"], env.MemoryModules.Select(x => Row(B(x.CapacityBytes), (x.ConfiguredSpeedMHz is > 0 ? x.ConfiguredSpeedMHz : x.SpeedMHz) is { } s && s > 0 ? $"{s} MHz" : "—", x.Manufacturer, x.PartNumber))));
        details.Append(Table("GPU", ["名前", "ドライバー", "ビデオメモリ"], env.Gpus.Select(g => Row(g.Name, g.DriverVersion, g.MemoryBytes is > 0 ? B(g.MemoryBytes) : "—"))));
        details.Append(Table("ページファイル", ["場所", "割り当て", "使用中"], env.PageFiles.Select(p => Row(p.Path, p.AllocatedMiB is { } al ? $"{al:N0} MiB" : "—", p.CurrentUsageMiB is { } cu ? $"{cu:N0} MiB" : "—"))));
        var settings = new List<string>();
        if (env.NtfsLastAccessUpdate is { } access) settings.Add(Row("NTFS: 最終アクセス日時の更新", access));
        if (env.NtfsShortNames is { } shortNames) settings.Add(Row("NTFS: 短いファイル名（8.3形式）", shortNames));
        if (env.OfflineFilesEnabled is { } offline) settings.Add(Row("オフラインファイル", $"{On(offline)}（動作中 {On(env.OfflineFilesActive)}）"));
        foreach (var (key, value) in env.SmbClient.OrderBy(p => p.Key, StringComparer.Ordinal)) settings.Add(Row("SMBクライアント: " + key, value));
        details.Append(Table("設定", ["項目", "値"], settings));
        if (env.FilterDrivers.Count > 0) details.Append($"<h3>フィルタードライバー（fltmc filters）</h3><pre>{E(string.Join("\n", env.FilterDrivers))}</pre>");
        if (env.Problems.Count > 0) details.Append($"<h3>読めなかったもの</h3><ul>{string.Concat(env.Problems.Select(p => $"<li>{E(p)}</li>"))}</ul>");

        static string Lines(IEnumerable<string> items) => string.Join(" ／ ", items);
        return $"""
<section><h2>計測したPCと環境</h2>
<p><b>PC</b>: {E(Lines(pc))}</p>
<p><b>ストレージ</b>: {(storage.Count == 0 ? "—" : string.Join("<br>", storage))}</p>
<p><b>I/Oに割り込むもの</b>: {(around.Count == 0 ? "—" : Lines(around))}</p>
<details class="env"><summary>詳しく見る（environment.json）</summary>{details}</details></section>
""";
    }
}
