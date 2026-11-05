using System.Text;

namespace ScreenRec.Core.Recording;

/// <summary>Writes Motion-JPEG frames into an AVI (RIFF, OpenDML-free, &lt; 4 GB) that Windows, VLC, QuickTime-with-plugins,
/// ffmpeg and Guardian's video intake read. No codec dependency: the frames are already JPEGs. Frames are written as they come;
/// the headers are patched with the final counts on <see cref="Dispose"/>.</summary>
public sealed class MjpegAviWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _w;
    private readonly int _width, _height;
    private readonly double _fps;
    private readonly List<(uint Offset, uint Size)> _index = new();
    private long _riffSizePos, _moviSizePos, _moviStart, _totalFramesPos, _streamLengthPos, _maxBytesPos, _suggestedPos, _streamSuggestedPos;
    private uint _maxFrame;
    private bool _closed;

    public const long MaxBytes = 3_900_000_000;

    public MjpegAviWriter(string path, int width, int height, double fps)
    {
        _width = width;
        _height = height;
        _fps = fps <= 0 ? 1 : fps;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _w = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        WriteHeaders();
    }

    public int FrameCount => _index.Count;
    public long Length => _stream.Length;
    public bool IsFull => _stream.Length >= MaxBytes;

    private void Fourcc(string code) => _w.Write(Encoding.ASCII.GetBytes(code));

    private void WriteHeaders()
    {
        var microPerFrame = (uint)Math.Round(1_000_000 / _fps);
        // Rate/scale for the stream: fps as a fraction with 1/1000 precision.
        var scale = 1000u;
        var rate = (uint)Math.Round(_fps * 1000);

        Fourcc("RIFF"); _riffSizePos = _stream.Position; _w.Write(0u); Fourcc("AVI ");
        Fourcc("LIST"); var hdrlSizePos = _stream.Position; _w.Write(0u); Fourcc("hdrl");
        Fourcc("avih"); _w.Write(56u);
        _w.Write(microPerFrame);
        _maxBytesPos = _stream.Position; _w.Write(0u); // max bytes per sec
        _w.Write(0u); // padding granularity
        _w.Write(0x10u); // AVIF_HASINDEX
        _totalFramesPos = _stream.Position; _w.Write(0u);
        _w.Write(0u); // initial frames
        _w.Write(1u); // streams
        _suggestedPos = _stream.Position; _w.Write(0u);
        _w.Write((uint)_width); _w.Write((uint)_height);
        _w.Write(0u); _w.Write(0u); _w.Write(0u); _w.Write(0u);

        Fourcc("LIST"); var strlSizePos = _stream.Position; _w.Write(0u); Fourcc("strl");
        Fourcc("strh"); _w.Write(56u);
        Fourcc("vids"); Fourcc("MJPG");
        _w.Write(0u); // flags
        _w.Write((ushort)0); _w.Write((ushort)0); // priority, language
        _w.Write(0u); // initial frames
        _w.Write(scale); _w.Write(rate);
        _w.Write(0u); // start
        _streamLengthPos = _stream.Position; _w.Write(0u);
        _streamSuggestedPos = _stream.Position; _w.Write(0u);
        _w.Write(uint.MaxValue); // quality
        _w.Write(0u); // sample size
        _w.Write((short)0); _w.Write((short)0); _w.Write((short)_width); _w.Write((short)_height);

        Fourcc("strf"); _w.Write(40u);
        _w.Write(40u); _w.Write(_width); _w.Write(_height);
        _w.Write((ushort)1); _w.Write((ushort)24);
        Fourcc("MJPG");
        _w.Write((uint)(_width * _height * 3));
        _w.Write(0); _w.Write(0); _w.Write(0u); _w.Write(0u);
        PatchSize(strlSizePos);
        PatchSize(hdrlSizePos);

        Fourcc("LIST"); _moviSizePos = _stream.Position; _w.Write(0u); Fourcc("movi");
        _moviStart = _stream.Position - 4;
    }

    private void PatchSize(long sizePos)
    {
        var end = _stream.Position;
        _stream.Position = sizePos;
        _w.Write((uint)(end - sizePos - 4));
        _stream.Position = end;
    }

    public void WriteFrame(ReadOnlySpan<byte> jpeg)
    {
        if (_closed) throw new ObjectDisposedException(nameof(MjpegAviWriter));
        var offset = (uint)(_stream.Position - _moviStart);
        Fourcc("00dc");
        _w.Write((uint)jpeg.Length);
        _w.Write(jpeg);
        if ((jpeg.Length & 1) == 1) _w.Write((byte)0);
        _index.Add((offset, (uint)jpeg.Length));
        _maxFrame = Math.Max(_maxFrame, (uint)jpeg.Length);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        PatchSize(_moviSizePos);
        Fourcc("idx1"); _w.Write((uint)(_index.Count * 16));
        foreach (var (offset, size) in _index)
        {
            Fourcc("00dc"); _w.Write(0x10u); _w.Write(offset); _w.Write(size);
        }
        PatchSize(_riffSizePos);
        void At(long pos, uint value) { _stream.Position = pos; _w.Write(value); }
        At(_totalFramesPos, (uint)_index.Count);
        At(_streamLengthPos, (uint)_index.Count);
        At(_suggestedPos, _maxFrame);
        At(_streamSuggestedPos, _maxFrame);
        At(_maxBytesPos, (uint)Math.Min(uint.MaxValue, _maxFrame * Math.Ceiling(_fps)));
        _w.Flush();
        _w.Dispose();
        _stream.Dispose();
    }
}
