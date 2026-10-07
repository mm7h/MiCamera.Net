namespace MiCamera.Net.Media.Runtime;

internal readonly record struct SourceOrigin(long Sequence, uint Timestamp90Khz, uint Duration90Khz);
