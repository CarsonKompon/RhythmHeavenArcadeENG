namespace M4Text;

/// <summary>
/// Sega NAOMI M4 cart stream-cipher codec, ported from MAME's naomim4.cpp
/// (Olivier Galibert, Andreas Naive; BSD-3-Clause).
///
/// The cipher is a stream cipher built on a 16-bit block SP-network (2 identical
/// rounds, 4 fixed 4-to-4 s-boxes per round, nibble diffusion). A 32-bit key
/// (two 16-bit subkeys) is stored in the security PIC (317-0503-jpn.ic3). The IV
/// is index-based and resets to 0 every 16 words (32 bytes), so each aligned
/// 0x20 block is independent — which is what makes in-place editing tractable.
/// </summary>
public sealed class M4Codec
{
    // Bytes per independent block. The hardware resets the IV every 16 words.
    public const int BlockSize = 32;
    private const int WordsPerBlock = 16;

    // Fixed 4-to-4 s-boxes (verbatim from naomim4.cpp k_sboxes[4][16]).
    private static readonly byte[][] KSboxes =
    {
        new byte[] { 9, 8, 2, 11, 1, 14, 5, 15, 12, 6, 0, 3, 7, 13, 10, 4 },
        new byte[] { 2, 10, 0, 15, 14, 1, 11, 3, 7, 12, 13, 8, 4, 9, 5, 6 },
        new byte[] { 4, 11, 3, 8, 7, 2, 15, 13, 1, 5, 14, 9, 6, 12, 0, 10 },
        new byte[] { 1, 13, 8, 2, 0, 5, 6, 14, 4, 11, 15, 10, 12, 3, 7, 9 },
    };

    private readonly ushort[] _oneRound;    // forward SP-network round
    private readonly ushort[] _oneRoundInv; // inverse, for re-encryption
    private readonly ushort _subkey1;
    private readonly ushort _subkey2;

    public ushort Subkey1 => _subkey1;
    public ushort Subkey2 => _subkey2;

    /// <summary>
    /// Builds a codec from the raw PIC dump (317-0503-jpn.ic3). Subkeys live at
    /// fixed offsets 0x5e0/0x5e2 (subkey1) and 0x5e4/0x5e6 (subkey2), little-endian.
    /// </summary>
    public M4Codec(byte[] picKeyData)
    {
        if (picKeyData.Length < 0x5e8)
            throw new ArgumentException($"PIC key data too small ({picKeyData.Length} bytes); need >= 0x5e8.", nameof(picKeyData));

        _subkey1 = (ushort)((picKeyData[0x5e2] << 8) | picKeyData[0x5e0]);
        _subkey2 = (ushort)((picKeyData[0x5e6] << 8) | picKeyData[0x5e4]);

        _oneRound = BuildOneRoundTable();
        _oneRoundInv = new ushort[0x10000];
        for (int i = 0; i < 0x10000; i++)
            _oneRoundInv[_oneRound[i]] = (ushort)i;
    }

    // Port of naomim4.cpp enc_init(): precompute the SP-network round for all inputs.
    private static ushort[] BuildOneRoundTable()
    {
        var table = new ushort[0x10000];
        Span<byte> inputNibble = stackalloc byte[4];
        Span<byte> outputNibble = stackalloc byte[4];
        for (int roundInput = 0; roundInput < 0x10000; roundInput++)
        {
            for (int n = 0; n < 4; n++)
            {
                inputNibble[n] = (byte)((roundInput >> (n * 4)) & 0xf);
                outputNibble[n] = 0;
            }

            byte aux = inputNibble[3];
            for (int n = 0; n < 4; n++) // 4 s-boxes per round
            {
                aux ^= KSboxes[n][inputNibble[n]];
                for (int i = 0; i < 4; i++) // bit diffusion
                    outputNibble[(n - i) & 3] |= (byte)(aux & (1 << i));
            }

            ushort result = 0;
            for (int n = 0; n < 4; n++)
                result |= (ushort)(outputNibble[n] << (4 * n));
            table[roundInput] = result;
        }
        return table;
    }

    // decrypt_one_round(word, subkey) = one_round[word ^ subkey] ^ subkey.
    private ushort DecryptOneRound(ushort word, ushort subkey)
        => (ushort)(_oneRound[word ^ subkey] ^ subkey);

    // Inverse of DecryptOneRound, used to re-encrypt edited plaintext.
    private ushort EncryptOneRound(ushort word, ushort subkey)
        => (ushort)(_oneRoundInv[word ^ subkey] ^ subkey);

    /// <summary>
    /// Decrypts an encrypted ROM image in place. Treated as a continuous word
    /// stream from offset 0 with the IV reset every 32 bytes (each aligned 0x20
    /// block is independent). Input length must be even.
    /// </summary>
    public void Decrypt(Span<byte> data)
    {
        if ((data.Length & 1) != 0)
            throw new ArgumentException("Data length must be even (16-bit words).", nameof(data));

        ushort iv = 0;
        int counter = 0;
        for (int i = 0; i < data.Length; i += 2)
        {
            ushort enc = (ushort)(data[i] | (data[i + 1] << 8));
            ushort dec = iv;
            iv = DecryptOneRound((ushort)(enc ^ iv), _subkey1);
            dec ^= DecryptOneRound(iv, _subkey2);

            data[i] = (byte)dec;
            data[i + 1] = (byte)(dec >> 8);

            if (++counter == WordsPerBlock)
            {
                counter = 0;
                iv = 0;
            }
        }
    }

    /// <summary>
    /// Re-encrypts a plaintext ROM image in place — exact inverse of <see cref="Decrypt"/>.
    /// </summary>
    public void Encrypt(Span<byte> data)
    {
        if ((data.Length & 1) != 0)
            throw new ArgumentException("Data length must be even (16-bit words).", nameof(data));

        ushort iv = 0;
        int counter = 0;
        for (int i = 0; i < data.Length; i += 2)
        {
            ushort dec = (ushort)(data[i] | (data[i + 1] << 8));
            ushort ivNew = EncryptOneRound((ushort)(dec ^ iv), _subkey2);
            ushort enc = (ushort)(EncryptOneRound(ivNew, _subkey1) ^ iv);

            data[i] = (byte)enc;
            data[i + 1] = (byte)(enc >> 8);
            iv = ivNew;

            if (++counter == WordsPerBlock)
            {
                counter = 0;
                iv = 0;
            }
        }
    }
}
