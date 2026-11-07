namespace GemStone.Gci;

/// <summary>
/// <c>loginFlags</c> for <c>GciLoginEx</c> (values from <c>gci.ht</c>, see
/// gemstone-c/07-gembuilder-c-function-reference/GciLoginEx.md).
/// </summary>
/// <remarks>
/// <c>GCI_TS_CLIENT</c> (0x40) and <c>GCI_WINDOWS_CLIENT</c> (0x200) are set by the GCI
/// library itself and are deliberately not exposed.
/// </remarks>
[Flags]
public enum GciLoginFlags : uint
{
    None = 0,

    /// <summary>GCI_LOGIN_PW_ENCRYPTED — password was encrypted with GciEncrypt.</summary>
    PasswordEncrypted = 0x1,

    /// <summary>GCI_LOGIN_IS_SUBORDINATE — child of the current session (RPC only).</summary>
    Subordinate = 0x2,

    /// <summary>GCI_LOGIN_FULL_COMPRESSION_ENABLED.</summary>
    FullCompression = 0x4,

    /// <summary>GCI_LOGIN_ERRS_USE_REF_SET — error OOPs go to the ReferencedSet instead of the PureExportSet.</summary>
    ErrorsUseReferencedSet = 0x8,

    /// <summary>GCI_LOGIN_QUIET — suppress the login banner the library prints to stdout.</summary>
    Quiet = 0x10,

    /// <summary>GCI_PASSWORDLESS_LOGIN — Kerberos / GSSAPI.</summary>
    PasswordlessKerberos = 0x80,

    /// <summary>GCI_LOGIN_SOLO — no stone required.</summary>
    Solo = 0x100,

    /// <summary>GCI_ONETIME_PASSWORD.</summary>
    OneTimePassword = 0x400,

    /// <summary>GCI_JWT_PASSWORD — password is a JSON web token.</summary>
    JwtPassword = 0x800,

    /// <summary>GCI_LOGIN_PW_FILE — password argument is a file name on the client.</summary>
    PasswordFromFile = 0x1000,

    /// <summary>GCI_LOGIN_PW_ENVVAR — password argument names a client environment variable.</summary>
    PasswordFromEnvironment = 0x2000,

    /// <summary>GCI_LOGIN_DEBUG — write debug info to the gem log.</summary>
    Debug = 0x4000,
}
