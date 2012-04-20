using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;

namespace Microsoft.Xna.Framework.Graphics
{
	internal partial class DXPreshader
	{
        public static DXPreshader CreatePreshader(BinaryReader reader)
        {           
            var inputRegs = (int)reader.ReadByte();
            var tempRegs = (int)reader.ReadByte();

            var symbolCount = (int)reader.ReadByte();
            var symbols = new MojoShader.MOJOSHADER_symbol[symbolCount];
            for (var s = 0; s < symbols.Length; s++)
            {
                symbols[s].name = reader.ReadString();

                symbols[s].register_set = (MojoShader.MOJOSHADER_symbolRegisterSet)reader.ReadByte();
                symbols[s].register_index = reader.ReadUInt16();
                symbols[s].register_count = reader.ReadByte();
            }

            var literalCount = (int)reader.ReadByte();
            var literals = new double[literalCount];
            for (var l = 0; l < literals.Length; l++)
                literals[l] = reader.ReadDouble();

            var instructionCount = (int)reader.ReadByte();
            var instructions = new MOJOSHADER_preshaderInstruction[instructionCount];
            for (var i = 0; i < instructions.Length; i++)
            {
                instructions[i].opcode = (MOJOSHADER_preshaderOpcode)reader.ReadByte();
                instructions[i].element_count = reader.ReadByte();
                instructions[i].operand_count = reader.ReadByte();

                for (var o = 0; o < instructions[i].operand_count; o++)
                {
                    var op = new MOJOSHADER_preshaderOperand();

                    op.type = (MOJOSHADER_preshaderOperandType)reader.ReadByte();
                    op.index = reader.ReadByte();
                    op.indexingType = reader.ReadByte();
                    op.indexingIndex = reader.ReadByte();

                    instructions[i].operand(o, op);
                }
            }

            var preshader = new DXPreshader();
            preshader._inRegs = new float[4 * inputRegs];
            preshader._temps = new double[tempRegs];
            preshader._symbols = symbols;
            preshader._literals = literals;
            preshader._instructions = instructions;

            return preshader;
        }

        //TODO: Fix mojoshader to handle output registers properly
        // TODO TODO:  What does this todo mean?

		public void Run(EffectParameterCollection parameters, float[] outRegs) 
        {
			//todo: cleanup and merge with DXShader :/
			
			//only populate modified stuff?
            foreach (var symbol in _symbols) 
            {
				if (symbol.register_set != MojoShader.MOJOSHADER_symbolRegisterSet.MOJOSHADER_SYMREGSET_FLOAT4)
					throw new NotImplementedException();

                var parameter = parameters[symbol.name];
				var data = parameter.GetValueSingleArray();
				
				switch (parameter.ParameterClass) 
                {
				case EffectParameterClass.Scalar:
					for (var i=0; i<data.Length; i++) 
                    {
						// Preshader scalar arrays map one to each vec4 register
                        _inRegs[symbol.register_index * 4 + i * 4] = (float)data[i];
					}
					break;
				case EffectParameterClass.Vector:
				case EffectParameterClass.Matrix:
					if (parameter.Elements.Count > 0) 
						throw new NotImplementedException();

					for (var y=0; y<Math.Min (symbol.register_count, parameter.RowCount); y++)
                        for (var x = 0; x < parameter.ColumnCount; x++)
                            _inRegs[(symbol.register_index + y) * 4 + x] = (float)data[y * 4 + x];
					break;
				default:
					throw new NotImplementedException();
				}
			}

            RunPreshader(_inRegs, outRegs);
            //MojoShader.NativeMethods.MOJOSHADER_runPreshader(ref _preshader, _inRegs, outRegs);
		}

        private void RunPreshader(float[] inRegs, float[] outRegs)
        {
            // this is fairly straightforward, as there aren't any branching
            //  opcodes in the preshader instruction set (at the moment, at least).
            const int scalarstart = (int)MOJOSHADER_preshaderOpcode.SCALAR_OPS;
            
            // TODO: Allocate this once at startup.
            var dst = new double [] { 0, 0, 0, 0 };
            var src = new double [,] { { 0, 0, 0, 0 }, { 0, 0, 0, 0 }, { 0, 0, 0, 0 } };

            for (var ip = 0; ip < _instructions.Length; ip++)
            {
                var inst = _instructions[ip];

                //MOJOSHADER_preshaderOperand operand = inst.operands;
                var elems = inst.element_count;
                //var elemsbytes = sizeof (double) * elems;
                var isscalarop = ((int)inst.opcode >= scalarstart);

                //assert(elems >= 0);
                //assert(elems <= 4);

                var operand = new MOJOSHADER_preshaderOperand();

                // load up our operands...
                int opiter, elemiter;
                for (opiter = 0; opiter < inst.operand_count-1; opiter++)
                {
                    operand = inst.operand(opiter);

                    var isscalar = ((isscalarop) && (opiter == 0));
                    var index = operand.index;
                    switch (operand.type)
                    {
                        case MOJOSHADER_preshaderOperandType.MOJOSHADER_PRESHADEROPERAND_LITERAL:
                        {
                            Debug.Assert((index + elems) <= _literals.Length);

                            if (!isscalar)
                                for ( var cpy=0; cpy < elems; cpy++ )
                                    src[opiter, cpy] = _literals[index + cpy];
                            else
                            {
                                for (elemiter = 0; elemiter < elems; elemiter++)
                                    src[opiter, elemiter] = _literals[index];
                            }

                            break;
                        }

                        case MOJOSHADER_preshaderOperandType.MOJOSHADER_PRESHADEROPERAND_INPUT:
                            if (operand.indexingType == 2)
                                index += (uint)inRegs[operand.indexingIndex] * 4;
                            if (isscalar)
                                src[opiter, 0] = inRegs[index];
                            else
                            {
                                for (var cpy = 0; cpy < elems; cpy++)
                                    src[opiter, cpy] = inRegs[index + cpy];
                            }
                            break;

                        case MOJOSHADER_preshaderOperandType.MOJOSHADER_PRESHADEROPERAND_OUTPUT:
                            if (isscalar)
                                src[opiter,0] = outRegs[index];
                            else
                            {
                                int cpy;
                                for (cpy = 0; cpy < elems; cpy++)
                                    src[opiter,cpy] = outRegs[index+cpy];
                            }
                            break;

                        case MOJOSHADER_preshaderOperandType.MOJOSHADER_PRESHADEROPERAND_TEMP:
                                if (isscalar)
                                    src[opiter, 0] = _temps[index];
                                else
                                    for ( var cpy=0; cpy < elems; cpy++ )
                                        src[opiter, cpy] = _temps[index + cpy];
                            break;

                        default:
                            Debug.Assert(false, "unexpected preshader operand type.");
                            return;

                    }
                }

                // run the actual instruction, store result to dst.
                switch (inst.opcode)
                {
                    case MOJOSHADER_preshaderOpcode.NOP:
                        break;

                    case MOJOSHADER_preshaderOpcode.MOV:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.NEG:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = -src[0,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.RCP:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = 1.0 / src[0,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.FRC:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,i] - Math.Floor(src[0,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.EXP:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = Math.Exp(src[0,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.LOG:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = Math.Log(src[0,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.RSQ:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = 1.0 / Math.Sqrt(src[0,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.SIN:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = Math.Sin(src[0,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.COS:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = Math.Cos(src[0,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.ASIN:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = Math.Asin(src[0,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.ACOS:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = Math.Acos(src[0,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.ATAN:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = Math.Atan(src[0,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.MIN:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,i] < src[1,i] ? src[0,i] : src[1,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.MAX:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,i] > src[1,i] ? src[0,i] : src[1,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.LT:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,i] < src[1,i] ? 1.0 : 0.0;
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.GE:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,i] >= src[1,i] ? 1.0 : 0.0;
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.ADD:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,i] + src[1,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.MUL:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,i] * src[1,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.DIV:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,i] / src[1,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.ATAN2:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = Math.Atan2(src[0,i], src[1,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.CMP:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,i] >= 0.0 ? src[1,i] : src[2,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.MIN_SCALAR:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,0] < src[1,i] ? src[0,0] : src[1,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.MAX_SCALAR:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,0] > src[1,i] ? src[0,0] : src[1,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.LT_SCALAR:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,0] < src[1,i] ? 1.0 : 0.0;
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.GE_SCALAR:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,0] >= src[1,i] ? 1.0 : 0.0;
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.ADD_SCALAR:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,0] + src[1,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.MUL_SCALAR:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,0] * src[1,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.DIV_SCALAR:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = src[0,0] / src[1,i];
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.ATAN2_SCALAR:
                    {
                        for (var i = 0; i < elems; i++) 
                            dst[i] = Math.Atan2(src[0,0], src[1,i]);
                        break; 
                    }

                    case MOJOSHADER_preshaderOpcode.DOT:
                    {
                        var final = 0.0;
                        for (var i = 0; i < elems; i++)
                            final += src[0,i] * src[1,i];
                        for (var i = 0; i < elems; i++)
                            dst[i] = final;  // !!! FIXME: is this right?

                        break;
                    }

                    case MOJOSHADER_preshaderOpcode.MOVC:
                    case MOJOSHADER_preshaderOpcode.NOISE:
                    case MOJOSHADER_preshaderOpcode.DOT_SCALAR: // Just a MUL?
                    case MOJOSHADER_preshaderOpcode.NOISE_SCALAR:
                    throw new Exception("Unimplemented preshader opcode!");
                    break;

                    default:
                        throw new Exception("Unknown preshader opcode!");
                        break;
                }

                operand = inst.operand(opiter);

                // Figure out where dst wants to be stored.
                if (operand.type == MOJOSHADER_preshaderOperandType.MOJOSHADER_PRESHADEROPERAND_TEMP)
                {
                    //assert(_temps.Length >= operand->index + (elemsbytes / sizeof (double)));

                    for (var i = 0; i < elems; i++)
                        _temps[operand.index + i] = dst[i];
                }
                else
                {
                    Debug.Assert(operand.type == MOJOSHADER_preshaderOperandType.MOJOSHADER_PRESHADEROPERAND_OUTPUT);

                    for (var i = 0; i < elems; i++)
                        outRegs[operand.index + i] = (float)dst[i];
                }
            }
        }
	}
}
