﻿using Altar.Unpack;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using static Altar.SR;

namespace Altar.Decomp
{
    // http://undertale.rawr.ws/decompilation
    // https://gitlab.com/snippets/14943

    public unsafe static class Disassembler
    {
        public static CodeInfo DisassembleCode(GMFileContent content, uint id)
        {
            if (id >= content.Code->Count)
                throw new ArgumentOutOfRangeException(nameof(id));

            var cee = (CodeEntryE*)GMFile.PtrFromOffset(content, (&content.Code->Offsets)[id]);

            var len = cee->Length;
            var bc = &cee->Bytecode;

            if (content.General->BytecodeVersion > 0xE)
            {
                var cef = (CodeEntryF*)cee;

                bc = (uint*)((byte*)&cef->BytecodeOffset + cef->BytecodeOffset); // ikr?
            }

            var ret = new List<IntPtr>(); // doesn't like T* as type arg

            len = Utils.PadTo(len, 4);
            AnyInstruction* instr;

            for (uint i = 0; i * 4 < len; /* see loop end */)
            {
                instr = (AnyInstruction*)(bc + i);

                ret.Add((IntPtr)instr);

                i += DisasmExt.Size(instr, content.General->BytecodeVersion);
            }

            return new CodeInfo
            {
                Name         = SectionReader.StringFromOffset(content, cee->Name),
                Instructions = Utils.MPtrListToPtrArr(ret),
                Size         = cee->Length
            };
        }

        public static Dictionary<IntPtr, int> GetReferenceTable(GMFileContent content, ReferenceDef[] defs)
        {
            var ret = new Dictionary<IntPtr, int>(defs.Length);

            for (int i = 0; i < defs.Length; i++)
            {
                var offTotal = (long)defs[i].FirstOffset;
                var addr     = (AnyInstruction*)GMFile.PtrFromOffset(content, offTotal);

                if (defs[i].Occurrences != 0) defs[i].VariableType = (VariableType)(((uint*)addr)[1] >> 24);

                for (int j = 0; j < defs[i].Occurrences /*&& curOffset != 0*/; j++)
                {
                    ret.Add((IntPtr)addr, i);

                    if (j < defs[i].Occurrences - 1) // at least one more iteration afterwards
                    {
                        var off = ((uint*)addr)[1] & 0x00FFFFFFL;

                        addr = (AnyInstruction*)GMFile.PtrFromOffset(content, offTotal += off); //! '+=', not '+'
                    }
                }
            }

            return ret;
        }

        public static string DisplayInstructions(GMFileContent content, RefData rdata, CodeInfo code, AnyInstruction*[] instructions = null, bool absolute = false)
        {
            var bcv = content.General->BytecodeVersion;

            var instrs = instructions ?? code.Instructions;

            if (instrs.Length == 0)
                return String.Empty;

            var sb = new StringBuilder();

            var firstI = code.Instructions[0];

            for (int i = 0; i < instrs.Length; i++)
            {
                var iptr = instrs[i];
                var relInstr = (long)iptr - (absolute ? (long)content.RawData.BPtr : (long)firstI);

                sb  .Append(HEX_PRE).Append(relInstr.ToString(HEX_FM6))
                    .Append(COLON_S).Append(iptr->OpCode.ToPrettyString(bcv)).Append(' ');

                switch (iptr->Kind(content.General->BytecodeVersion))
                {
                    case InstructionKind.SingleType:
                        var st = iptr->SingleType;

                        sb.Append(st.Type.ToPrettyString());

                        if (bcv > 0xE && st.OpCode.VersionF == FOpCode.Dup)
                            sb.Append(' ').Append(st.DupExtra);

                        break;
                    case InstructionKind.DoubleType:
                        var dt = iptr->DoubleType;

                        if (bcv > 0xE && iptr->OpCode.VersionF == FOpCode.Cmp)
                            sb.Append(dt.ComparisonType.ToPrettyString()).Append(' ');

                        sb.Append(dt.Types);
                        break;
                    case InstructionKind.Goto:
                        var g = iptr->Goto;

                        if ((g.Offset.UValue&0x800000) != 0)
                        {
                            // there is precisely one place where this happens: gml_Object_obj_battlebomb_Alarm_3.gml
                            // and I have no clue what it could mean
                        }
                        else
                        {
                            var a = g.Offset.UValue * 4;
                            if ((a & 0xFF000000) != 0)
                            {
                                a &= 0x00FFFFFF;
                                a -= 0x01000000;
                            }

                            sb.Append(HEX_PRE).Append(Utils.ToHexSignString(relInstr + unchecked((int)a), HEX_FM6));
                        }
                        break;

                    #region set
                    case InstructionKind.Set:
                        var s = iptr->Set;

                        sb.Append(s.Types).Append(' ');

                        if (s.IsMagic)
                        {
                            sb.Append(MAGIC);
                            break;
                        }

                        if (s.Instance <= InstanceType.StackTopOrGlobal)
                            sb.Append(s.Instance.ToPrettyString());
                        else
                        {
                            var o = SectionReader.GetObjectInfo(content, (uint)s.Instance, true);

                            sb.Append('[').Append(o.Name).Append(']');
                        }

                        sb.Append(':');

                        sb.Append(rdata.Variables[rdata.VarAccessors[(IntPtr)iptr]].Name);
                        sb.Append(s.DestVar.Type.ToPrettyString());

                        if (true)
                        {
                            sb.Append(' ');
                            sb.Append(rdata.VarAccessors[(IntPtr)iptr]);
                        }

                        break;
                    #endregion
                    #region push
                    case InstructionKind.Push:
                        var pp = (PushInstruction*)iptr;
                        var p = iptr->Push;

                        sb.Append(p.Type.ToPrettyString()).Append(' ');

                        var r = p.ValueRest;

                        switch (p.Type)
                        {
                            case DataType.Int16:
                                sb.Append(p.Value.ToString(CultureInfo.InvariantCulture));
                                break;
                            case DataType.Variable:
                                var rv = *(Reference*)&r;

                                var inst = (InstanceType)p.Value;

                                if (inst <= InstanceType.StackTopOrGlobal)
                                    sb.Append(inst.ToPrettyString());
                                else
                                {
                                    var o = SectionReader.GetObjectInfo(content, (uint)inst, true);

                                    sb.Append('[').Append(o.Name).Append(']');
                                }
                                sb.Append(':');

                                sb.Append(rdata.Variables[rdata.VarAccessors[(IntPtr)iptr]].Name);
                                sb.Append(rv.Type.ToPrettyString());

                                if (true)
                                {
                                    sb.Append(' ');
                                    sb.Append(rdata.VarAccessors[(IntPtr)iptr]);
                                }

                                break;
                            case DataType.Boolean:
                                sb.Append(((DwordBool*)&r)->ToPrettyString());
                                break;
                            case DataType.Double:
                                sb.Append(((double*)&r)->ToString(DOUBLE_FMT, CultureInfo.InvariantCulture));
                                break;
                            case DataType.Single:
                                sb.Append(((float*)&r)->ToString(SINGLE_FMT, CultureInfo.InvariantCulture));
                                break;
                            case DataType.Int32:
                                sb.Append(unchecked((int)r).ToString(CultureInfo.InvariantCulture));
                                break;
                            case DataType.Int64:
                                sb.Append(((long*)&pp->ValueRest)->ToString(CultureInfo.InvariantCulture));
                                break;
                            case DataType.String:
                                sb.Append(SectionReader.GetStringInfo(content, (uint)p.ValueRest).Escape());
                                break;
                        }
                        break;
                    #endregion
                    #region call
                    case InstructionKind.Call:
                        var c = iptr->Call;

                        sb.Append(c.ReturnType.ToPrettyString()).Append(':')
                            .Append(c.Arguments).Append(' ');

                        sb.Append(rdata.Functions[rdata.FuncAccessors[(IntPtr)iptr]].Name);
                        sb.Append(c.Function.Type.ToPrettyString());
                        break;
                    #endregion

                    case InstructionKind.Break:
                        var b = iptr->Break;

                        sb.Append(b.Type.ToPrettyString()).Append(' ').Append(b.Signal);
                        break;
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
        public static string DisplayInstructions(GMFile file, int id, bool absolute = false) => DisplayInstructions(file.Content, file.RefData, file.Code[id], absolute: absolute);
    }
}
