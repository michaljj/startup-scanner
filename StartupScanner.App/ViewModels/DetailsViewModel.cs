using System;
using System.Collections.Generic;
using System.Globalization;
using System.ComponentModel;
using System.Linq;
using StartupScanner.Core;
using StartupScanner.Core.Safety;
using StartupScanner.Core.Models;

namespace StartupScanner.App.ViewModels;

/// <summary>
/// One labelled row in the panel. A named type rather than a ValueTuple because WPF binds to
/// properties and a tuple's Item1/Item2 are fields, so tuple bindings silently render nothing.
/// </summary>
public sealed record DetailRow(string Label, string Value)
{
    public static implicit operator DetailRow((string Label, string Value) pair) => new(pair.Label, pair.Value);
}

public sealed class DetailsViewModel(ScannerHost host) : INotifyPropertyChanged
{
    private static readonly DetailRow[] Empty = Array.Empty<DetailRow>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasSelection { get; private set; }
    public string Title { get; private set; } = "Select an entry";

    public IReadOnlyList<DetailRow> Identity { get; private set; } = Empty;
    public IReadOnlyList<DetailRow> File { get; private set; } = Empty;
    public IReadOnlyList<DetailRow> Signature { get; private set; } = Empty;
    public IReadOnlyList<DetailRow> LocationAndState { get; private set; } = Empty;
    public IReadOnlyList<DetailRow> TypeSpecific { get; private set; } = Empty;
    public IReadOnlyList<string> HistoryLines { get; private set; } = Array.Empty<string>();

    public void Show(StartupItem? item)
    {
        if (item is null)
        {
            HasSelection = false;
            Title = "Select an entry";
            Identity = File = Signature = LocationAndState = TypeSpecific = Empty;
            HistoryLines = Array.Empty<string>();
            RaiseAll();
            return;
        }

        HasSelection = true;
        Title = item.Name;

        // FileFacts are computed here rather than during the scan: this is the "lazily computed" path.
        var facts = item.ImagePath is null ? null : host.Files.Get(item.ImagePath);
        var firstSeen = host.History.FirstSeenUtc(item.Id);

        Identity = new DetailRow[]
        {
            ("Name", item.Name),
            ("Provider", item.ProviderId),
            ("Category", item.Category.ToString()),
            ("Raw value", item.RawValue),
            ("Image path", item.ImagePath ?? "—"),
            ("Arguments", item.Arguments ?? "—"),
            ("Expanded", item.ImagePath ?? "—"),
            ("Runs as", item.UserSid ?? "machine-wide"),
            ("First seen", firstSeen?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "this scan"),
        };

        File = facts is null || !facts.Exists
            ? new DetailRow[] { ("Exists", facts is null ? "no image path" : "file not found") }
            : new DetailRow[]
            {
                ("Exists", "yes"),
                ("SHA-256", facts.Sha256 ?? "unreadable"),
                ("Size", facts.Size.ToString("N0", CultureInfo.InvariantCulture) + " bytes"),
                ("Created", Stamp(facts.CreatedUtc)),
                ("Modified", Stamp(facts.ModifiedUtc)),
                ("Company", facts.Version?.Company ?? "—"),
                ("Product", facts.Version?.Product ?? "—"),
                ("Description", facts.Version?.FileDescription ?? "—"),
                ("File version", facts.Version?.FileVersion ?? "—"),
                ("ACL owner", facts.AclOwner ?? "—"),
            };

        Signature = facts?.Signature is { } signature
            ? new DetailRow[]
            {
                ("Status", signature.Status.ToString()),
                ("Signer", signature.SignerName ?? "—"),
                ("Chain", signature.Chain.Count == 0 ? "—" : string.Join(" → ", signature.Chain)),
                ("Countersigned", Stamp(signature.TimestampUtc)),
                ("Source", signature.IsCatalogSigned ? "catalog" : "embedded"),
                ("Microsoft-signed", signature.IsMicrosoftSigned ? "yes" : "no"),
                ("Detail", signature.StatusMessage ?? "—"),
            }
            : Empty;

        var block = Guardrails.Evaluate(item);
        LocationAndState = new DetailRow[]
        {
            ("Location", item.Location),
            ("State", item.State.ToString()),
            ("Controlled by", item.StateSource),
            ("Risk tier", item.Risk.ToString()),
            ("Writable", block.Blocked ? "no" : "yes"),
            ("Reason", block.Reason ?? "—"),
            ("Partially read", item.PartiallyRead ? "yes" : "no"),
        };

        TypeSpecific = BuildTypeSpecific(item);
        HistoryLines = BuildHistory(item);
        RaiseAll();
    }

    private IReadOnlyList<DetailRow> BuildTypeSpecific(StartupItem item)
    {
        var rows = item.Extra
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value) && pair.Key != "Xml")
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new DetailRow(pair.Key, pair.Value))
            .ToList();

        if (item.Category is StartupCategory.Service or StartupCategory.Driver)
            rows.Insert(0, new DetailRow("Running state", host.ServiceStatus.GetStatus(item.Name) ?? "unknown"));

        return rows;
    }

    private IReadOnlyList<string> BuildHistory(StartupItem item)
    {
        var lines = host.History.ReadFor(item.Id)
            .Select(e => $"{e.Ts.ToLocalTime():yyyy-MM-dd HH:mm}  {e.Event}  {e.Before ?? "—"} → {e.After ?? "—"}")
            .ToList();

        lines.AddRange(host.Journal.All
            .Where(r => r.ItemId == item.Id)
            .Select(r => $"{r.Ts.ToLocalTime():yyyy-MM-dd HH:mm}  write via {r.Mechanism}  " +
                         $"{(r.Enabled ? "enabled" : "disabled")}"));

        return lines.Count == 0 ? new[] { "No recorded history for this entry." } : lines;
    }

    private static string Stamp(DateTime? value)
        => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "—";

    private void RaiseAll()
    {
        foreach (var property in new[]
                 {
                     nameof(HasSelection), nameof(Title), nameof(Identity), nameof(File), nameof(Signature),
                     nameof(LocationAndState), nameof(TypeSpecific), nameof(HistoryLines),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
