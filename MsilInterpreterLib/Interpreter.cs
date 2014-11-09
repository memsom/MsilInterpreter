﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace MsilInterpreterLib
{
    public class Interpreter
    {
        internal ILParser Parser { get; } = new ILParser();
        /// <summary>
        /// Using a stack of objects removes necessity to cast stored values to the tightest representation.
        /// For example, ldc.i4.s loads Int32 like ldc.i4 does instead of byte because casting it to object is causing overhead no matter which type. 
        /// </summary>
        internal Stack<object> Stack { get; } = new Stack<object>(); 
        internal Heap Heap { get; } = new Heap(10, 100);
        internal object[] Locals { get; } = new object[255];

        public void Run(Action action)
        {   
            var instructions = Parser.ParseILFromMethod(action.GetMethodInfo()).ToList();
            if (!instructions.Any())
                return;

            var offsetToIndexMapping = instructions.Select((instruction, index) => new { instruction.Offset, Index = index})
                                                   .ToDictionary(x => x.Offset, x => x.Index);

            var flowIndexer = 0;
            while (flowIndexer < instructions.Count)
            {
                var currentInstruction = instructions[flowIndexer];

                switch (currentInstruction.Code.FlowControl)
                {
                    case FlowControl.Branch:
                    case FlowControl.Cond_Branch:
                        var jumpTo = InterpretBranchInstruction(currentInstruction);
                        if (jumpTo == -1)
                        {
                            flowIndexer++;
                            continue;
                        }
                        flowIndexer = offsetToIndexMapping[jumpTo];
                        break;
                    case FlowControl.Break:
                        throw new NotImplementedException(currentInstruction + " is not supported yet");
                    case FlowControl.Call:
                        InterpretCallInstruction(currentInstruction);
                        flowIndexer++;
                        break;
                    case FlowControl.Meta:
                        throw new NotImplementedException(currentInstruction + " is not supported yet");
                    case FlowControl.Next:
                        InterpretNextInstruction(currentInstruction);
                        flowIndexer++;
                        break;
                    case FlowControl.Return:
                        if (currentInstruction.Code.Name == "ret")
                            return;
                        throw new NotImplementedException(currentInstruction + " is not supported yet");
                    case FlowControl.Throw:
                        throw new NotImplementedException(currentInstruction + " is not supported yet");
                }
            }
        }

        private void InterpretNextInstruction(ILInstruction instruction)
        {
            switch (instruction.Code.Name)
            {
                case "add":
                {
                    dynamic v1 = Stack.Pop();
                    dynamic v2 = Stack.Pop();
                    Stack.Push(v1 + v2);
                    break;
                }
                case "ceq":
                {
                    dynamic v1 = Stack.Pop();
                    dynamic v2 = Stack.Pop();
                    Stack.Push(v1 == v2 ? 1 : 0);
                    break;
                }
                case "clt":
                {
                    dynamic v1 = Stack.Pop();
                    dynamic v2 = Stack.Pop();
                    Stack.Push(v2 < v1 ? 1 : 0);
                    break;
                }
                case "ldc.i4.0": Stack.Push(0); break;
                case "ldc.i4.1": Stack.Push(1); break;
                case "ldc.i4.2": Stack.Push(2); break;
                case "ldc.i4.3": Stack.Push(3); break;
                case "ldc.i4.4": Stack.Push(4); break;
                case "ldc.i4.5": Stack.Push(5); break;
                case "ldc.i4.6": Stack.Push(6); break;
                case "ldc.i4.7": Stack.Push(7); break;
                case "ldc.i4.8": Stack.Push(8); break;
                case "ldc.i4.m1": Stack.Push(-1); break;
                case "ldc.i4.s": Stack.Push(Convert.ToInt32(instruction.Operand)); break;
                case "ldc.i8":
                case "ldc.r4":
                case "ldc.r8": Stack.Push(instruction.Operand); break;
                case "ldloc.0": PushLocalToStack(0); break;
                case "ldloc.1": PushLocalToStack(1); break;
                case "ldloc.2": PushLocalToStack(2); break;
                case "ldloc.3": PushLocalToStack(3); break;
                case "ldloc.s": PushLocalToStack((byte) instruction.Operand); break;
                case "mul":
                {
                    dynamic v1 = Stack.Pop();
                    dynamic v2 = Stack.Pop();
                    Stack.Push(v1 * v2);
                    break;
                }
                case "nop": break;
                case "stloc.0": PopFromStackToLocal(0); break;
                case "stloc.1": PopFromStackToLocal(1); break;
                case "stloc.2": PopFromStackToLocal(2); break;
                case "stloc.3": PopFromStackToLocal(3); break;
                case "stloc.s": PopFromStackToLocal((byte) instruction.Operand); break;
                case "sub":
                {
                    dynamic v1 = Stack.Pop();
                    dynamic v2 = Stack.Pop();
                    Stack.Push(v2 - v1);
                    break;
                }
                default:
                    throw new NotImplementedException(instruction.Code.Name + " is not implemented.");
            }
        }

        private void InterpretCallInstruction(ILInstruction instruction)
        {
            switch (instruction.Code.Name)
            {
                case "call":
                    var method = instruction.Operand as MethodInfo;
                    if (method == null) break;

                    var arguments = method.GetParameters().Select(param => Stack.Pop()).ToArray();
                    Array.Reverse(arguments);

                    object result = null;
                    if (method.IsStatic)
                    {
                        result = method.Invoke(null, arguments);
                    }

                    if (result != null)
                        Stack.Push(result);
                    break;
                default:
                    throw new NotImplementedException(instruction.Code.Name + " is not implemented.");
            }
        }

        private int InterpretBranchInstruction(ILInstruction instruction)
        {
            switch (instruction.Code.Name)
            {
                case "br":
                case "br.s":
                    return (int) instruction.Operand;
                case "brfalse":
                case "brfalse.s":
                    if ((int) Stack.Pop() == 0)
                        return (int) instruction.Operand;

                    return -1;
                case "brtrue":
                case "brtrue.s":
                    if ((int) Stack.Pop() == 1)
                        return (int) instruction.Operand;
                    
                    return -1;
                default:
                    throw new NotImplementedException(instruction.Code.Name + " is not implemented.");
            }
        }

        private void PushLocalToStack(byte index) => Stack.Push(Locals[index]);
        private void PopFromStackToLocal(byte index) => Locals[index] = Stack.Pop();
    }
}