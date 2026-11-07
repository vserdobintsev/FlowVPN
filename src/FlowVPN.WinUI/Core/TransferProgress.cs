namespace FlowVPN;

public enum TransferStage { Downloading, Verifying, Applying }
public sealed record TransferProgress(string Item, TransferStage Stage, long BytesReceived = 0, long? TotalBytes = null);
