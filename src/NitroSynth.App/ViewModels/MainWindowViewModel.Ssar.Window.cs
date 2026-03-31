using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NAudio.Midi;

namespace NitroSynth.App.ViewModels;

public partial class MainWindowViewModel
{
    public readonly record struct SsarSseqWindowSession(
        string Title,
        string SsarDisplay,
        string SequenceDisplay,
        string SbnkDisplay,
        int Volume,
        int ChannelPriority,
        int PlayerPriority,
        string PlayerDisplay,
        string DecompiledText,
        int SsarId,
        int SequenceId,
        string ExportBaseName,
        ushort SbnkId,
        byte PlayerId,
        byte[] BaseEventData,
        int PlaybackStartOffset);

    public bool TryCreateSsarSequenceWindowSession(SsarSequenceRow? row, out SsarSseqWindowSession session)
    {
        session = default;

        if (row is null)
        {
            StatusMessage = "SSAR sequence is not selected.";
            return false;
        }

        if (SelectedSsar is null)
        {
            StatusMessage = "SSAR is not selected.";
            return false;
        }

        if (_lastInfo is null || !_lastInfo.Ssar.TryGetValue(SelectedSsar.Id, out var ssarInfo))
        {
            StatusMessage = "Selected SSAR info is unavailable.";
            return false;
        }

        if (!TryReadFileFromFat(ssarInfo.FileId, out var ssarBytes))
        {
            StatusMessage = $"Could not read SSAR data (FileId={ssarInfo.FileId}).";
            return false;
        }

        SSAR ssar;
        try
        {
            ssar = SSAR.Read(ssarBytes);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to parse SSAR: {ex.Message}";
            return false;
        }

        var sequence = ssar.Sequences.FirstOrDefault(s => s.Index == row.Id);
        if (sequence is null)
        {
            StatusMessage = $"SSAR sequence not found: {row.Id:D3}.";
            return false;
        }

        if (!TryBuildSsarPlaybackData(ssar, ssarBytes, sequence, out var playbackData, out var playbackStartOffset, out var extractError))
        {
            StatusMessage = extractError ?? "Failed to extract SSAR sequence event data.";
            return false;
        }

        string sequenceLabel = string.IsNullOrWhiteSpace(row.Name) ? $"SEQ {row.Id:D3}" : row.Name;
        string exportBaseName = SanitizeExportBaseName($"{SelectedSsar.Name}_{sequenceLabel}");

        if (!TryDecompileStandaloneSseq(playbackData, exportBaseName, out var decompiledText, out var decompileError))
        {
            StatusMessage = decompileError ?? "Failed to decode SSAR sequence.";
            return false;
        }

        string sbnkName = ResolveSbnkName(sequence.SbnkId);
        string playerName = ResolvePlayerName(sequence.PlayerId);

        session = new SsarSseqWindowSession(
            Title: $"SSAR SEQ - {SelectedSsar.Id:D3}:{row.Id:D3}",
            SsarDisplay: $"{SelectedSsar.Id:D3}: {SelectedSsar.Name} (FileId={ssarInfo.FileId})",
            SequenceDisplay: $"{row.Id:D3}: {sequenceLabel}",
            SbnkDisplay: $"{sequence.SbnkId:D3}: {sbnkName}",
            Volume: sequence.Volume,
            ChannelPriority: sequence.ChannelPriority,
            PlayerPriority: sequence.PlayerPriority,
            PlayerDisplay: $"{sequence.PlayerId:D3}: {playerName}",
            DecompiledText: decompiledText,
            SsarId: SelectedSsar.Id,
            SequenceId: row.Id,
            ExportBaseName: exportBaseName,
            SbnkId: sequence.SbnkId,
            PlayerId: sequence.PlayerId,
            BaseEventData: (byte[])playbackData.Clone(),
            PlaybackStartOffset: playbackStartOffset);

        return true;
    }

    public async Task StartSsarSequencePlaybackAsync(
        byte[] eventData,
        int track0StartOffset,
        int sbnkId,
        string displayName,
        int sequencePriorityBase = 0,
        int? playerId = null)
    {
        if (eventData is null || eventData.Length == 0)
        {
            StatusMessage = "SSAR playback failed: sequence data is empty.";
            return;
        }

        var playbackBank = BankOptions.FirstOrDefault(b => b.Id == sbnkId) ?? new SBNK.BankOption(sbnkId, ResolveSbnkName(sbnkId));
        SelectedSseqBank = playbackBank;

        _sseqPauseRequested = false;
        IsSseqPaused = false;
        ClearPauseResumeState();

        if (playerId.HasValue)
            SelectMixerPlayerById(playerId.Value);

        await StopSelectedSseqPlaybackAsync();

        if (!await PrepareSelectedSseqPlaybackBankAsync())
            return;

        int playbackSeed = Guid.NewGuid().GetHashCode();
        await RunSseqPlaybackSessionAsync(
            (byte[])eventData.Clone(),
            startState: null,
            playbackSeed: playbackSeed,
            startStatus: $"SSEQ playback started: {displayName}",
            track0StartOffset: track0StartOffset,
            playbackLabel: displayName,
            sequencePriorityBase: sequencePriorityBase);
    }

    public async Task ToggleSsarSequencePauseAsync()
    {
        await ToggleSelectedSseqPauseAsync();
    }

    public void StopSsarSequencePlayback()
    {
        StopSelectedSseqPlayback();
    }

    public bool TryCompileSseqTextAgainstBase(byte[] baseEventData, string editedText, out byte[] editedEventData, out string? error)
    {
        editedEventData = Array.Empty<byte>();
        error = null;

        if (baseEventData is null || baseEventData.Length == 0)
        {
            error = "SSEQ data is empty.";
            return false;
        }

        var instructionLengthMap = BuildSseqInstructionLengthMap(baseEventData);
        if (instructionLengthMap.Count == 0)
        {
            error = "SSEQ compile error: instruction map is empty.";
            return false;
        }

        var orderedOffsets = instructionLengthMap.Keys
            .OrderBy(x => x)
            .ToArray();

        var editedCommands = BuildSseqCommandMap(editedText, orderedOffsets, out var labels, out var mapError);
        if (mapError is not null)
        {
            error = mapError;
            return false;
        }

        if (editedCommands.Count == 0)
        {
            error = "SSEQ compile error: no instruction lines were found.";
            return false;
        }

        foreach (int requiredOffset in instructionLengthMap.Keys)
        {
            if (!editedCommands.ContainsKey(requiredOffset))
            {
                error = $"SSEQ compile error: missing instruction at 0x{requiredOffset:X6}.";
                return false;
            }
        }

        var patched = (byte[])baseEventData.Clone();

        foreach (var (offset, editedCommandText) in editedCommands)
        {
            if (!instructionLengthMap.TryGetValue(offset, out int expectedLength))
            {
                error = $"SSEQ compile error: 0x{offset:X6} is not an existing instruction offset.";
                return false;
            }

            if (!TryAssembleSseqCommand(editedCommandText, labels, out var assembled, out var assembleError))
            {
                error = $"SSEQ compile error at 0x{offset:X6}: {assembleError}";
                return false;
            }

            if (assembled.Length != expectedLength)
            {
                error = $"SSEQ compile error at 0x{offset:X6}: instruction byte length changed ({expectedLength} -> {assembled.Length}).";
                return false;
            }

            if (offset < 0 || offset + expectedLength > patched.Length)
            {
                error = $"SSEQ compile error at 0x{offset:X6}: write range is out of bounds.";
                return false;
            }

            Buffer.BlockCopy(assembled, 0, patched, offset, expectedLength);
        }

        editedEventData = patched;
        return true;
    }

    public bool TryDecompileStandaloneSseq(byte[] eventData, string baseName, out string decompiledText, out string? error)
    {
        decompiledText = string.Empty;
        error = null;

        if (eventData is null || eventData.Length == 0)
        {
            error = "SSEQ data is empty.";
            return false;
        }

        try
        {
            var pseudoSseqBytes = BuildStandaloneSseqBytes(eventData);
            var parsed = SSEQ.Read(pseudoSseqBytes);
            decompiledText = parsed.Decompile(baseName, $"{baseName}.smft");
            return true;
        }
        catch (Exception ex)
        {
            error = $"SSEQ decompile failed: {ex.Message}";
            return false;
        }
    }

    public byte[] BuildStandaloneSseqBytes(byte[] eventData)
    {
        if (eventData is null)
            throw new ArgumentNullException(nameof(eventData));

        var pseudoHeader = new SSEQ.HeaderInfo
        {
            Magic = "SSEQ",
            ByteOrder = 0xFEFF,
            Version = 0x0100,
            HeaderSize = 0x10,
            BlockCount = 1,
            DataMagic = "DATA",
            DataOffset = 0x1C
        };

        return BuildSseqBinary(pseudoHeader, eventData);
    }

    public bool ExportStandaloneSseq(string outputPath, SseqExportKind kind, byte[] eventData, string smftText, out string message)
    {
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            message = "EXPORT failed: output path is empty.";
            return false;
        }

        try
        {
            switch (kind)
            {
                case SseqExportKind.Sseq:
                {
                    var bytes = BuildStandaloneSseqBytes(eventData);
                    File.WriteAllBytes(outputPath, bytes);
                    message = $"Exported SSEQ: {outputPath}";
                    return true;
                }
                case SseqExportKind.Smft:
                {
                    File.WriteAllText(outputPath, smftText ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    message = $"Exported SMFT: {outputPath}";
                    return true;
                }
                case SseqExportKind.Midi:
                {
                    if (!TryBuildSseqMidiEventCollection(eventData, out var collection, out var midiError, out var midiWarning))
                    {
                        message = midiError ?? "EXPORT failed: could not convert to MIDI.";
                        return false;
                    }

                    MidiFile.Export(outputPath, collection);
                    message = string.IsNullOrWhiteSpace(midiWarning)
                        ? $"Exported MIDI: {outputPath}"
                        : $"Exported MIDI (truncated): {outputPath} ({midiWarning})";
                    return true;
                }
                default:
                    message = "EXPORT failed: unknown format.";
                    return false;
            }
        }
        catch (Exception ex)
        {
            message = $"EXPORT failed: {ex.Message}";
            return false;
        }
    }

    public bool ExportStandaloneSseqWithStatus(string outputPath, SseqExportKind kind, byte[] eventData, string smftText)
    {
        bool success = ExportStandaloneSseq(outputPath, kind, eventData, smftText, out var message);
        StatusMessage = message;
        return success;
    }

    public void NotifySsarSequenceSaveNotImplemented()
    {
        StatusMessage = "SSAR sequence SAVE is not implemented yet.";
    }

    public void NotifySsarSequenceImportNotImplemented()
    {
        StatusMessage = "SSAR sequence REPLACE is not implemented yet.";
    }

    private static bool TryBuildSsarPlaybackData(
        SSAR ssar,
        byte[] ssarBytes,
        SSAR.SequenceInfo sequence,
        out byte[] eventData,
        out int startOffset,
        out string? error)
    {
        eventData = Array.Empty<byte>();
        startOffset = 0;
        error = null;

        int dataStart = (int)ssar.Header.DataOffset;
        if (dataStart < 0 || dataStart >= ssarBytes.Length)
        {
            error = $"Invalid SSAR DATA offset: 0x{ssar.Header.DataOffset:X8}.";
            return false;
        }

        int dataLength = ssarBytes.Length - dataStart;
        if (dataLength <= 0)
        {
            error = "SSAR DATA block is empty.";
            return false;
        }

        int start = ResolveRelativeOffset(sequence, dataStart, dataLength);
        if (start < 0 || start >= dataLength)
        {
            error = $"Invalid SSAR sequence start offset (raw=0x{sequence.SequenceOffsetRaw:X8}, abs=0x{sequence.SequenceOffset:X6}).";
            return false;
        }

        int end = dataLength;
        foreach (var other in ssar.Sequences)
        {
            int candidate = ResolveRelativeOffset(other, dataStart, dataLength);
            if (candidate <= start)
                continue;

            if (candidate < end)
                end = candidate;
        }

        if (end <= start || end > dataLength)
            end = dataLength;

        int length = end - start;
        if (length <= 0)
        {
            error = $"Could not resolve SSAR sequence range (start=0x{start:X6}, end=0x{end:X6}).";
            return false;
        }

        eventData = new byte[length];
        Buffer.BlockCopy(ssarBytes, dataStart + start, eventData, 0, length);
        if (!TryRebaseRelativeAddresses(eventData, absoluteBaseOffset: start, out error))
        {
            eventData = Array.Empty<byte>();
            return false;
        }

        startOffset = 0;

        return true;
    }

    private static int ResolveRelativeOffset(SSAR.SequenceInfo sequence, int dataStart, int dataLength)
    {
        if (sequence.SequenceOffsetRaw != 0xFFFFFFFF)
        {
            int raw = (int)sequence.SequenceOffsetRaw;
            if (raw >= 0 && raw < dataLength)
                return raw;
        }

        int absoluteRelative = sequence.SequenceOffset - dataStart;
        if (absoluteRelative >= 0 && absoluteRelative < dataLength)
            return absoluteRelative;

        return -1;
    }

    private static bool TryRebaseRelativeAddresses(byte[] eventData, int absoluteBaseOffset, out string? error)
    {
        error = null;

        if (eventData.Length == 0)
            return true;

        int offset = 0;
        while (offset < eventData.Length)
        {
            if (!SseqEventDecoder.TryDecode(eventData, offset, out var decoded, out _))
            {
                offset++;
                continue;
            }

            switch (decoded.Kind)
            {
                case SseqDecodedKind.OpenTrack:
                {
                    int localDest = decoded.Value1 - absoluteBaseOffset;
                    if (localDest < 0 || localDest >= eventData.Length)
                    {
                        error = $"SSAR sequence contains opentrack destination outside extracted range: 0x{decoded.Value1:X6}.";
                        return false;
                    }

                    WriteU24(eventData, offset + 2, localDest);
                    break;
                }
                case SseqDecodedKind.Jump:
                case SseqDecodedKind.Call:
                {
                    int localDest = decoded.Value0 - absoluteBaseOffset;
                    if (localDest < 0 || localDest >= eventData.Length)
                    {
                        error = $"SSAR sequence contains {decoded.Kind.ToString().ToLowerInvariant()} destination outside extracted range: 0x{decoded.Value0:X6}.";
                        return false;
                    }

                    WriteU24(eventData, offset + 1, localDest);
                    break;
                }
            }

            offset += decoded.Length;
        }

        return true;
    }

    private static void WriteU24(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
    }

    private string ResolveSbnkName(int sbnkId)
    {
        if (_lastSymb is not null && _lastSymb.Sbnk.TryGetValue(sbnkId, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        var bankOption = BankOptions.FirstOrDefault(b => b.Id == sbnkId);
        if (bankOption is not null && !string.IsNullOrWhiteSpace(bankOption.Name))
            return bankOption.Name;

        return $"SBNK {sbnkId:D3}";
    }

    private string ResolvePlayerName(int playerId)
    {
        if (_lastSymb is not null && _lastSymb.Player.TryGetValue(playerId, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        var playerOption = SseqPlayerOptions.FirstOrDefault(p => p.Id == playerId);
        if (playerOption is not null && !string.IsNullOrWhiteSpace(playerOption.Name))
            return playerOption.Name;

        return $"PLAYER {playerId:D3}";
    }
}
