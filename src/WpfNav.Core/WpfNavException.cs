namespace WpfNav.Core;

/// <summary>A failure the caller can act on. The message is one actionable sentence.</summary>
public sealed class WpfNavException(string message) : Exception(message);
