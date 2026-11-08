using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace UsbIpClient;

/**
 * Vendor and product names from the public USB ID database (usb.ids from linux-usb.org,
 * dual-licensed GPL-2.0-or-later / BSD-3-Clause), bundled gzipped as an embedded resource.
 */
public static class UsbIds
{
    private static readonly Lazy<(Dictionary<ushort, string> vendors, Dictionary<uint, string> products)> Db = new(Load);

    public static string? Vendor(ushort vid) => Db.Value.vendors.TryGetValue(vid, out var v) ? v : null;

    public static string? Product(ushort vid, ushort pid) =>
        Db.Value.products.TryGetValue(((uint)vid << 16) | pid, out var p) ? p : null;

    /** Short vendor name: "Logitech, Inc." -> "Logitech", "Micro Star International" stays. */
    public static string? ShortVendor(ushort vid)
    {
        var v = Vendor(vid);
        if (v == null) return null;
        foreach (var suffix in new[] { ", Inc.", " Inc.", ", Ltd", " Co., Ltd", " Corp.", ", Corp.", " Corporation", " GmbH", " AG", " Ltd." })
        {
            var i = v.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (i > 0) v = v[..i];
        }
        return v.Trim().TrimEnd(',');
    }

    private static (Dictionary<ushort, string>, Dictionary<uint, string>) Load()
    {
        var vendors = new Dictionary<ushort, string>();
        var products = new Dictionary<uint, string>();
        try
        {
            using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("UsbIpClient.usb.ids.gz");
            if (res == null) return (vendors, products);
            using var gz = new GZipStream(res, CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            ushort vendor = 0;
            var inVendors = false;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] != '\t')
                {
                    // Vendor line "046d  Logitech, Inc."; anything else ("C 00 ...") ends the vendor section.
                    inVendors = line.Length > 6 && ushort.TryParse(line.AsSpan(0, 4), System.Globalization.NumberStyles.HexNumber, null, out vendor);
                    if (!inVendors && vendors.Count > 0) break;
                    if (inVendors) vendors[vendor] = line[6..].Trim();
                }
                else if (inVendors && line.Length > 7 && line[1] != '\t' &&
                         ushort.TryParse(line.AsSpan(1, 4), System.Globalization.NumberStyles.HexNumber, null, out var product))
                {
                    products[((uint)vendor << 16) | product] = line[7..].Trim();
                }
            }
        }
        catch
        {
            // names are a nicety
        }
        return (vendors, products);
    }
}
