using System.Text;

namespace M4Text;

/// <summary>
/// A focused SH-4 disassembler for tracing NAOMI game code. SH-4 instructions are
/// a fixed 16 bits, stored little-endian in this (little-endian) build, which makes
/// a hand-written decoder tractable. Coverage is the practical subset needed to
/// follow control flow and — crucially — resolve PC-relative literal-pool loads
/// (<c>mov.l @(disp,PC),Rn</c>), which is how 32-bit addresses/pointers appear.
/// Unrecognised words fall back to <c>.word 0xXXXX</c> rather than guessing.
/// </summary>
public sealed class Sh4Disassembler
{
    private readonly byte[] _rom;
    private readonly RomMemoryMap _map;

    public Sh4Disassembler(byte[] rom, RomMemoryMap map)
    {
        _rom = rom;
        _map = map;
    }

    private ushort ReadInsn(long off) => (ushort)(_rom[off] | (_rom[off + 1] << 8));
    private ushort ReadU16(long off) => (ushort)(_rom[off] | (_rom[off + 1] << 8));
    private uint ReadU32(long off) =>
        (uint)(_rom[off] | (_rom[off + 1] << 8) | (_rom[off + 2] << 16) | (_rom[off + 3] << 24));

    private static string R(int n) => "r" + n;

    /// <summary>Disassembles <paramref name="count"/> instructions starting at a ROM offset.</summary>
    public IEnumerable<string> Disassemble(long startRom, int count)
    {
        for (int i = 0; i < count; i++)
        {
            long off = startRom + i * 2;
            if (off + 2 > _rom.Length) yield break;
            yield return Line(off);
        }
    }

    /// <summary>Formats one instruction: ROM offset, RAM address, raw bytes, mnemonic, annotation.</summary>
    public string Line(long romOff)
    {
        ushort insn = ReadInsn(romOff);
        uint pc = _map.RomToRam(romOff) ?? 0;
        var sb = new StringBuilder();
        sb.Append($"0x{romOff:x8}  ");
        sb.Append(pc != 0 ? $"{pc:x8}  " : "--------  ");
        sb.Append($"{insn & 0xff:x2}{insn >> 8:x2}  ");
        sb.Append(Decode(insn, pc, out string? annotation).PadRight(28));
        if (annotation is not null) sb.Append("; ").Append(annotation);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Decodes a single 16-bit instruction to a mnemonic string.</summary>
    public string Decode(ushort insn, uint pc, out string? annotation)
    {
        annotation = null;
        int nib = insn >> 12;
        int n = (insn >> 8) & 0xF;
        int m = (insn >> 4) & 0xF;
        int d4 = insn & 0xF;
        int d8 = insn & 0xFF;
        int d12 = insn & 0xFFF;
        sbyte imm8 = (sbyte)(insn & 0xFF);

        switch (nib)
        {
            case 0x0:
                return Decode0(insn, n, m);

            case 0x1: // mov.l Rm,@(disp,Rn)  disp = d4*4
                return $"mov.l {R(m)}, @({d4 * 4},{R(n)})";

            case 0x2:
                return d4 switch
                {
                    0x0 => $"mov.b {R(m)}, @{R(n)}",
                    0x1 => $"mov.w {R(m)}, @{R(n)}",
                    0x2 => $"mov.l {R(m)}, @{R(n)}",
                    0x4 => $"mov.b {R(m)}, @-{R(n)}",
                    0x5 => $"mov.w {R(m)}, @-{R(n)}",
                    0x6 => $"mov.l {R(m)}, @-{R(n)}",
                    0x7 => $"div0s {R(m)}, {R(n)}",
                    0x8 => $"tst {R(m)}, {R(n)}",
                    0x9 => $"and {R(m)}, {R(n)}",
                    0xA => $"xor {R(m)}, {R(n)}",
                    0xB => $"or {R(m)}, {R(n)}",
                    0xC => $"cmp/str {R(m)}, {R(n)}",
                    0xD => $"xtrct {R(m)}, {R(n)}",
                    0xE => $"mulu.w {R(m)}, {R(n)}",
                    0xF => $"muls.w {R(m)}, {R(n)}",
                    _ => Word(insn),
                };

            case 0x3:
                return d4 switch
                {
                    0x0 => $"cmp/eq {R(m)}, {R(n)}",
                    0x2 => $"cmp/hs {R(m)}, {R(n)}",
                    0x3 => $"cmp/ge {R(m)}, {R(n)}",
                    0x4 => $"div1 {R(m)}, {R(n)}",
                    0x5 => $"dmulu.l {R(m)}, {R(n)}",
                    0x6 => $"cmp/hi {R(m)}, {R(n)}",
                    0x7 => $"cmp/gt {R(m)}, {R(n)}",
                    0x8 => $"sub {R(m)}, {R(n)}",
                    0xA => $"subc {R(m)}, {R(n)}",
                    0xB => $"subv {R(m)}, {R(n)}",
                    0xC => $"add {R(m)}, {R(n)}",
                    0xD => $"dmuls.l {R(m)}, {R(n)}",
                    0xE => $"addc {R(m)}, {R(n)}",
                    0xF => $"addv {R(m)}, {R(n)}",
                    _ => Word(insn),
                };

            case 0x4:
                return Decode4(insn, n);

            case 0x5: // mov.l @(disp,Rm),Rn  disp = d4*4
                return $"mov.l @({d4 * 4},{R(m)}), {R(n)}";

            case 0x6:
                return d4 switch
                {
                    0x0 => $"mov.b @{R(m)}, {R(n)}",
                    0x1 => $"mov.w @{R(m)}, {R(n)}",
                    0x2 => $"mov.l @{R(m)}, {R(n)}",
                    0x3 => $"mov {R(m)}, {R(n)}",
                    0x4 => $"mov.b @{R(m)}+, {R(n)}",
                    0x5 => $"mov.w @{R(m)}+, {R(n)}",
                    0x6 => $"mov.l @{R(m)}+, {R(n)}",
                    0x7 => $"not {R(m)}, {R(n)}",
                    0x8 => $"swap.b {R(m)}, {R(n)}",
                    0x9 => $"swap.w {R(m)}, {R(n)}",
                    0xA => $"negc {R(m)}, {R(n)}",
                    0xB => $"neg {R(m)}, {R(n)}",
                    0xC => $"extu.b {R(m)}, {R(n)}",
                    0xD => $"extu.w {R(m)}, {R(n)}",
                    0xE => $"exts.b {R(m)}, {R(n)}",
                    0xF => $"exts.w {R(m)}, {R(n)}",
                    _ => Word(insn),
                };

            case 0x7: // add #imm,Rn
                return $"add #{imm8}, {R(n)}";

            case 0x8:
                return Decode8(insn, n, m, d4, d8, pc, out annotation);

            case 0x9: // mov.w @(disp,PC),Rn
            {
                uint addr = pc + 4 + (uint)(d8 * 2);
                long? ro = _map.RamToRom(addr);
                if (ro is long r && r + 2 <= _rom.Length)
                {
                    short lit = (short)ReadU16(r);
                    annotation = $"=0x{(ushort)lit:x4} ({lit})";
                }
                return $"mov.w @(0x{addr:x8}), {R(n)}";
            }

            case 0xA: // bra disp12
            {
                int disp = (d12 << 20) >> 20; // sign-extend 12-bit
                uint target = (uint)(pc + 4 + disp * 2);
                annotation = $"-> 0x{target:x8}";
                return "bra 0x" + target.ToString("x8");
            }

            case 0xB: // bsr disp12
            {
                int disp = (d12 << 20) >> 20;
                uint target = (uint)(pc + 4 + disp * 2);
                annotation = $"-> 0x{target:x8}";
                return "bsr 0x" + target.ToString("x8");
            }

            case 0xC:
                return DecodeC(insn, n, m, d8, pc, out annotation);

            case 0xD: // mov.l @(disp,PC),Rn
            {
                uint addr = (uint)((pc & 0xFFFFFFFC) + 4 + d8 * 4);
                long? ro = _map.RamToRom(addr);
                if (ro is long r && r + 4 <= _rom.Length)
                {
                    uint lit = ReadU32(r);
                    long? litRom = _map.RamToRom(lit);
                    annotation = litRom is long lr
                        ? $"=0x{lit:x8} -> ROM 0x{lr:x8}"
                        : $"=0x{lit:x8}";
                }
                return $"mov.l @(0x{addr:x8}), {R(n)}";
            }

            case 0xE: // mov #imm,Rn
                return $"mov #{imm8}, {R(n)}";

            default:
                return Word(insn);
        }
    }

    private string Decode0(ushort insn, int n, int m)
    {
        // Fixed whole-word opcodes first.
        switch (insn)
        {
            case 0x0008: return "clrt";
            case 0x0009: return "nop";
            case 0x000B: return "rts";
            case 0x0018: return "sett";
            case 0x0019: return "div0u";
            case 0x001B: return "sleep";
            case 0x0028: return "clrmac";
            case 0x002B: return "rte";
            case 0x0048: return "clrs";
            case 0x0058: return "sets";
        }

        int lo = insn & 0xFF;
        return lo switch
        {
            0x02 => $"stc sr, {R(n)}",
            0x03 => $"bsrf {R(n)}",
            0x0A => $"sts mach, {R(n)}",
            0x12 => $"stc gbr, {R(n)}",
            0x1A => $"sts macl, {R(n)}",
            0x22 => $"stc vbr, {R(n)}",
            0x23 => $"braf {R(n)}",
            0x29 => $"movt {R(n)}",
            0x2A => $"sts pr, {R(n)}",
            0x04 => $"mov.b {R(m)}, @({R(0)},{R(n)})",
            0x05 => $"mov.w {R(m)}, @({R(0)},{R(n)})",
            0x06 => $"mov.l {R(m)}, @({R(0)},{R(n)})",
            0x07 => $"mul.l {R(m)}, {R(n)}",
            0x0C => $"mov.b @({R(0)},{R(m)}), {R(n)}",
            0x0D => $"mov.w @({R(0)},{R(m)}), {R(n)}",
            0x0E => $"mov.l @({R(0)},{R(m)}), {R(n)}",
            0x0F => $"mac.l @{R(m)}+, @{R(n)}+",
            _ => Word(insn),
        };
    }

    private string Decode4(ushort insn, int n)
    {
        int lo = insn & 0xFF;
        int m = (insn >> 4) & 0xF;
        return lo switch
        {
            0x00 => $"shll {R(n)}",
            0x01 => $"shlr {R(n)}",
            0x02 => $"sts.l mach, @-{R(n)}",
            0x04 => $"rotl {R(n)}",
            0x05 => $"rotr {R(n)}",
            0x06 => $"lds.l @{R(n)}+, mach",
            0x08 => $"shll2 {R(n)}",
            0x09 => $"shlr2 {R(n)}",
            0x0A => $"lds {R(n)}, mach",
            0x0B => $"jsr @{R(n)}",
            0x0E => $"ldc {R(n)}, sr",
            0x10 => $"dt {R(n)}",
            0x11 => $"cmp/pz {R(n)}",
            0x15 => $"cmp/pl {R(n)}",
            0x16 => $"lds.l @{R(n)}+, macl",
            0x18 => $"shll8 {R(n)}",
            0x19 => $"shlr8 {R(n)}",
            0x1A => $"lds {R(n)}, macl",
            0x1B => $"tas.b @{R(n)}",
            0x20 => $"shal {R(n)}",
            0x21 => $"shar {R(n)}",
            0x22 => $"sts.l pr, @-{R(n)}",
            0x24 => $"rotcl {R(n)}",
            0x25 => $"rotcr {R(n)}",
            0x26 => $"lds.l @{R(n)}+, pr",
            0x28 => $"shll16 {R(n)}",
            0x29 => $"shlr16 {R(n)}",
            0x2A => $"lds {R(n)}, pr",
            0x2B => $"jmp @{R(n)}",
            0x0C => $"shad {R(m)}, {R(n)}",
            0x0D => $"shld {R(m)}, {R(n)}",
            _ => Word(insn),
        };
    }

    private string Decode8(ushort insn, int n, int m, int d4, int d8, uint pc, out string? annotation)
    {
        annotation = null;
        int sub = (insn >> 8) & 0xF;
        switch (sub)
        {
            case 0x0: return $"mov.b {R(0)}, @({d4},{R(m)})";
            case 0x1: return $"mov.w {R(0)}, @({d4 * 2},{R(m)})";
            case 0x4: return $"mov.b @({d4},{R(m)}), {R(0)}";
            case 0x5: return $"mov.w @({d4 * 2},{R(m)}), {R(0)}";
            case 0x8: return $"cmp/eq #{(sbyte)d8}, {R(0)}";
            case 0x9: // bt
            case 0xB: // bf
            case 0xD: // bt/s
            case 0xF: // bf/s
            {
                int disp = (sbyte)d8;
                uint target = (uint)(pc + 4 + disp * 2);
                annotation = $"-> 0x{target:x8}";
                string mn = sub switch { 0x9 => "bt", 0xB => "bf", 0xD => "bt/s", _ => "bf/s" };
                return $"{mn} 0x{target:x8}";
            }
            default: return Word(insn);
        }
    }

    private string DecodeC(ushort insn, int n, int m, int d8, uint pc, out string? annotation)
    {
        annotation = null;
        int sub = (insn >> 8) & 0xF;
        switch (sub)
        {
            case 0x0: return $"mov.b {R(0)}, @({d8},gbr)";
            case 0x1: return $"mov.w {R(0)}, @({d8 * 2},gbr)";
            case 0x2: return $"mov.l {R(0)}, @({d8 * 4},gbr)";
            case 0x3: return $"trapa #{d8}";
            case 0x4: return $"mov.b @({d8},gbr), {R(0)}";
            case 0x5: return $"mov.w @({d8 * 2},gbr), {R(0)}";
            case 0x6: return $"mov.l @({d8 * 4},gbr), {R(0)}";
            case 0x7: // mova @(disp,PC),R0
            {
                uint addr = (uint)((pc & 0xFFFFFFFC) + 4 + d8 * 4);
                annotation = $"r0 = 0x{addr:x8}";
                return $"mova @(0x{addr:x8}), r0";
            }
            case 0x8: return $"tst #{d8}, {R(0)}";
            case 0x9: return $"and #{d8}, {R(0)}";
            case 0xA: return $"xor #{d8}, {R(0)}";
            case 0xB: return $"or #{d8}, {R(0)}";
            case 0xC: return $"tst.b #{d8}, @({R(0)},gbr)";
            case 0xD: return $"and.b #{d8}, @({R(0)},gbr)";
            case 0xE: return $"xor.b #{d8}, @({R(0)},gbr)";
            case 0xF: return $"or.b #{d8}, @({R(0)},gbr)";
            default: return Word(insn);
        }
    }

    private static string Word(ushort insn) => $".word 0x{insn:x4}";
}
