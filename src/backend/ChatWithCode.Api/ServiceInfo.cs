namespace ChatWithCode.Api;

/// <summary>Basic facts the extension shows on its settings page to confirm it reached the right backend.</summary>
public sealed record ServiceInfo(string Name, string Version, string Organization);
