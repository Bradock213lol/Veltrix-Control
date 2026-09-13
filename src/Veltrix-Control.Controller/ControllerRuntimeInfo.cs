namespace VeltrixControl.Controller;

public sealed record ControllerRuntimeInfo(int HttpsPort, string? CertificateSha256);
