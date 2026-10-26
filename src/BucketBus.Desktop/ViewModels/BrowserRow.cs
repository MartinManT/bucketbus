using System.Globalization;
using System.Text;
using BucketBus.Core;

namespace BucketBus.Desktop.ViewModels;

public enum BrowserRowKind { Bucket, Prefix, Object }

// Identity is never reconstructed from display text, a URI or a local path.
public sealed record BrowserRow(string Key, BrowserRowKind Kind, long? Size = null, DateTimeOffset? Modified = null)
{
    public string Name => BrowserText.Display(Key);
    public string ExactIdentity => BrowserText.Display(Key, asciiOnly: true);
    public string Type => Kind switch { BrowserRowKind.Bucket => "Bucket", BrowserRowKind.Prefix => "Prefix", _ => Key.EndsWith('/') ? "Marker object" : "Object" };
    public string SizeText => Kind == BrowserRowKind.Object ? ByteSizeFormatter.Format(Size) : "—";
    public string ExactSize => Kind == BrowserRowKind.Object ? ByteSizeFormatter.Exact(Size) : "Not applicable";
    public string ModifiedText => Modified?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "Unknown";
}

public static class BrowserText
{
    public static string Display(string value, bool asciiOnly = false)
    {
        var text = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\\') text.Append("\\\\");
            else if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format || char.IsSurrogate(c)
                || asciiOnly && c > 127)
                text.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else text.Append(c);
        }
        return text.ToString();
    }
    public static string Error(StorageError error) => error switch
    {
        StorageError.AccessDenied => "Access denied. For bucket discovery, enter a bucket you are allowed to use and choose Go.",
        StorageError.AuthenticationFailed => "Authentication failed. Check the selected profile's credentials and signing region.",
        StorageError.NotFound => "The requested bucket or object was not found.",
        StorageError.Timeout => "Request timed out within the read-only deadline. Refresh to retry.",
        StorageError.Cancelled => "Cancelled. Refresh to restart this listing.",
        StorageError.TlsFailure => "TLS validation failed. Verify the endpoint and certificate trust; no bypass is available.",
        StorageError.ProtocolError => "Invalid storage response or pagination state. Refresh to retry.",
        StorageError.RedirectRejected => "Storage redirect rejected. Review the profile endpoint and signing region.",
        StorageError.RetryLimit => "Read retry limit reached. Refresh to retry.",
        StorageError.PaginationLimit => "Listing page limit reached. Choose a narrower prefix.",
        StorageError.SessionExpired => "Profile or session changed. Reconnect explicitly.",
        StorageError.InvalidRequest => "Invalid bucket, prefix or addressing configuration. Review the fields.",
        StorageError.TransportFailure => "Network request failed. Check connectivity and refresh.",
        _ => "Storage request failed. Refresh to retry. Code BB-BROWSE."
    };
    public static string Credential(CredentialStatus status) => status switch
    {
        CredentialStatus.Missing => "Credentials missing. Open Profiles to enter credentials; session credentials do not survive restart.",
        CredentialStatus.Locked => "Secure store locked. Allow an unlock prompt and reconnect, or configure session-only credentials in Profiles.",
        CredentialStatus.Unavailable => "Secure store unavailable. Configure your keyring or explicitly choose session-only credentials in Profiles.",
        CredentialStatus.Denied => "Credential access denied. No plaintext fallback was used.",
        CredentialStatus.Cancelled => "Credential acquisition cancelled.",
        _ => "Credential retrieval failed. Review the selected profile. No plaintext fallback was used."
    };
}
