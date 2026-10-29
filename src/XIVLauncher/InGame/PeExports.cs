using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace XIVLauncher.InGame;

/// <summary>
///     从 DLL 文件的导出表读导出函数的 RVA。只读文件、不在本进程加载它（加载会跑 DllMain, 截取模块不能在启动器里初始化）
/// </summary>
public static class PeExports
{
    /// <summary>
    ///     按名字找导出函数的 RVA; 文件里没有这个导出返回 null。不是 PE 文件、导出表损坏、导出是转发项时抛 <see cref="InvalidDataException" />
    /// </summary>
    public static uint? FindRva(string dllPath, string exportName)
    {
        using var stream = File.OpenRead(dllPath);

        PEReader reader;

        try
        {
            reader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);
            _ = reader.PEHeaders;
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException($"{dllPath} 不是有效的 PE 文件: {ex.Message}", ex);
        }

        using (reader)
        {
            var directory = reader.PEHeaders.PEHeader?.ExportTableDirectory ?? default;

            if (directory.RelativeVirtualAddress == 0 || directory.Size == 0)
                return null;

            var table = Read(reader, directory.RelativeVirtualAddress, 40);

            var functionCount = BitConverter.ToUInt32(table, 0x14);
            var nameCount     = BitConverter.ToUInt32(table, 0x18);
            var functionsRva  = BitConverter.ToInt32(table, 0x1C);
            var namesRva      = BitConverter.ToInt32(table, 0x20);
            var ordinalsRva   = BitConverter.ToInt32(table, 0x24);

            if (nameCount == 0)
                return null;

            var names    = Read(reader, namesRva, checked((int)nameCount * 4));
            var ordinals = Read(reader, ordinalsRva, checked((int)nameCount * 2));

            for (var i = 0; i < nameCount; i++)
            {
                var nameRva = BitConverter.ToInt32(names, i * 4);

                if (!string.Equals(ReadName(reader, nameRva), exportName, StringComparison.Ordinal))
                    continue;

                var ordinal = BitConverter.ToUInt16(ordinals, i * 2);

                if (ordinal >= functionCount)
                    throw new InvalidDataException($"{dllPath} 的导出 {exportName} 序号 {ordinal} 超出函数表");

                var rva = BitConverter.ToUInt32(Read(reader, functionsRva + ordinal * 4, 4), 0);

                // 落在导出目录里的 RVA 是转发字符串（「模块.函数」）, 不是本模块里的代码
                if (rva >= directory.RelativeVirtualAddress && rva < directory.RelativeVirtualAddress + directory.Size)
                    throw new InvalidDataException($"{dllPath} 的导出 {exportName} 是转发项, 不能直接调用");

                return rva;
            }

            return null;
        }
    }

    private static byte[] Read(PEReader reader, int rva, int length)
    {
        var block = reader.GetSectionData(rva);

        if (block.Length < length)
            throw new InvalidDataException($"导出表数据越界 (RVA=0x{rva:X}, 长度 {length})");

        return block.GetReader().ReadBytes(length);
    }

    private static string ReadName(PEReader reader, int rva)
    {
        var blob  = reader.GetSectionData(rva).GetReader();
        var bytes = new List<byte>();

        while (blob.RemainingBytes > 0)
        {
            var value = blob.ReadByte();

            if (value == 0)
                return Encoding.ASCII.GetString(bytes.ToArray());

            bytes.Add(value);
        }

        throw new InvalidDataException($"导出名没有结尾 (RVA=0x{rva:X})");
    }
}
