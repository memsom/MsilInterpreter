﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using MsilInterpreterLib.Components;
using MsilInterpreterLib.Msil;

namespace MsilInterpreterLib
{
    internal class Interpreter
    {
        private readonly Runtime runtime;

        public Runtime Runtime { get { return runtime; } }
        public StackFrame CurrentStackFrame { get { return Runtime.CallStack.Peek(); } }

        public Interpreter(Runtime runtime)
        {
            this.runtime = runtime;
        }

        public void Execute(DotMethodBase method)
        {
            if (method.Body.Count == 0)
                method.Execute(this);
            else
                Interpret(method.Body);
            
            UnwindCallStack();
        }

        private void Interpret(IEnumerable<ILInstruction> methodBody)
        {
            var instructions = methodBody.ToList();
            var offsetToIndexMapping = instructions.Select((instruction, index) => new { instruction.Offset, Index = index })
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
                        throw new NotSupportedException(currentInstruction.ToString());
                    case FlowControl.Call:
                        InterpretCallInstruction(currentInstruction);
                        flowIndexer++;
                        break;
                    case FlowControl.Meta:
                        throw new NotSupportedException(currentInstruction.ToString());
                    case FlowControl.Next:
                        InterpretNextInstruction(currentInstruction);
                        flowIndexer++;
                        break;
                    case FlowControl.Return:
                        if (currentInstruction.Code.Name == "ret")
                            return;
                        throw new NotSupportedException(currentInstruction.ToString());
                    case FlowControl.Throw:
                        throw new NotSupportedException(currentInstruction.ToString());
                }
            }
        }

        private void InterpretNextInstruction(ILInstruction instruction)
        {
            switch (instruction.Code.Name)
            {
                case "add":
                {
                    dynamic op2 = PopFromStack();
                    dynamic op1 = PopFromStack();
                    PushToStack(op1 + op2);
                    break;
                }
                case "ceq":
                {
                    dynamic op2 = PopFromStack();
                    dynamic op1 = PopFromStack();
                    PushToStack(op1 == op2 ? 1 : 0);
                    break;
                }
                case "cgt":
                {
                    dynamic op2 = PopFromStack();
                    dynamic op1 = PopFromStack();
                    PushToStack(op1 > op2 ? 1 : 0);
                    break;
                }
                case "clt":
                {
                    dynamic value2 = PopFromStack();
                    dynamic value1 = PopFromStack();
                    PushToStack(value1 < value2 ? 1 : 0);
                    break;
                }
                case "conv.i4":
                {
                    var value = PopFromStack();
                    PushToStack(Convert.ToInt32(value));
                    break;
                }
                case "div":
                {
                    dynamic op2 = PopFromStack();
                    dynamic op1 = PopFromStack();
                    PushToStack(op1 / op2);
                    break;
                }
                case "dup":
                {
                    var value = PopFromStack();
                    var duplicate = value;
                    PushToStack(value);
                    PushToStack(duplicate);
                    break;
                }
                case "isinst":
                {
                    var objReference = PopFromStack();
                    // If the object reference itself is a null reference, then isinst likewise returns a null reference.
                    if (objReference == null)
                        PushToStack(null);

                    var objType = GetFromHeap((Guid) objReference).TypeHandler;
                    var possibleConversions = new List<DotType> { objType }; // TODO: test also against its base classes and interfaces
                    var testAgainstType = LookUpType(instruction.Operand as Type);
                    var result = possibleConversions.Contains(testAgainstType) ? objReference : null; // TODO: return casted reference
                    PushToStack(result);
                    break;
                }
                case "ldarg.0":
                case "ldarg.1":
                case "ldarg.2":
                case "ldarg.3":
                case "ldarg.s":
                {
                    var index = instruction.Code.Name.Split('.')[1];
                    int argPosition;
                    if (index == "s")
                        argPosition = (byte)instruction.Operand;
                    else
                        argPosition = Convert.ToInt32(index);

                    var paramPosition = argPosition;

                    if (!CurrentStackFrame.CurrentMethod.IsStatic)
                    {
                        if (argPosition > 0)
                            paramPosition--; // instance methods and ctors have the first parameter (instance they are being called on) hidden
                        else
                        {
                            PushToStack(CurrentStackFrame.Arguments[0]);
                            break;
                        }
                    }

                    var param = CurrentStackFrame.CurrentMethod.ParametersTypes[paramPosition];
                    if (param.IsValueType)
                    {
                        PushToStack(CurrentStackFrame.Arguments[argPosition]);
                    }
                    else
                    {
                        var reference = (Guid)CurrentStackFrame.Arguments[argPosition];
                        PushToStack(reference);
                    }
                    
                    break;
                }
                case "ldc.i4.0": PushToStack(0); break;
                case "ldc.i4.1": PushToStack(1); break;
                case "ldc.i4.2": PushToStack(2); break;
                case "ldc.i4.3": PushToStack(3); break;
                case "ldc.i4.4": PushToStack(4); break;
                case "ldc.i4.5": PushToStack(5); break;
                case "ldc.i4.6": PushToStack(6); break;
                case "ldc.i4.7": PushToStack(7); break;
                case "ldc.i4.8": PushToStack(8); break;
                case "ldc.i4.m1": PushToStack(-1); break;
                case "ldc.i4.s": PushToStack(Convert.ToInt32(instruction.Operand)); break;
                case "ldelem.i1":
                case "ldelem.i2":
                case "ldelem.i4":
                {
                    int index = (int) PopFromStack();
                    var arrayReference = (Guid) PopFromStack();
                    var array = GetFromHeap(arrayReference)["Values"] as int[];
                    PushToStack(array[index]);
                    break;
                }
                case "ldelem.ref":
                case "ldelema":
                {
                    int index = (int) PopFromStack();
                    var arrayInstance = GetFromHeap((Guid)PopFromStack());
                    var arrayValues = arrayInstance["Values"] as Guid[];
                    if (arrayValues[index] == Guid.Empty)
                    {
                        ObjectInstance instance;
                        var elementType = instruction.Operand as Type;
                        if (elementType != null)
                        {
                            arrayValues[index] = CreateObjectInstance(LookUpType(elementType), out instance);
                        }
                        else
                        {
                            arrayValues[index] = CreateObjectInstance(arrayInstance["ElementType"] as DotType, out instance);
                        }
                    }
                    PushToStack(arrayValues[index]);
                    break;
                }
                case "ldfld":
                {
                    var instanceRef = PopFromStack();
                    var instance = GetFromHeap((Guid)instanceRef);
                    var fieldName = (instruction.Operand as FieldInfo).Name;
                    PushToStack(instance[fieldName]);
                    break;
                }
                case "ldind.ref":
                {
                    // address is already on the stack
                    break;
                }
                case "ldlen":
                {
                    var arrayRef = PopFromStack();
                    var arrayInstance = GetFromHeap((Guid) arrayRef);
                    PushToStack(arrayInstance["Length"]);
                    break;
                }
                case "ldloc.0": PushLocalToStack(0); break;
                case "ldloc.1": PushLocalToStack(1); break;
                case "ldloc.2": PushLocalToStack(2); break;
                case "ldloc.3": PushLocalToStack(3); break;
                case "ldloc.s": PushLocalToStack((byte) instruction.Operand); break;
                case "ldnull": PushToStack(null); break;
                case "ldstr":
                {
                    ObjectInstance stringInstance;
                    var reference = CreateObjectInstance(LookUpType(typeof(string)), out stringInstance);
                    stringInstance["Value"] = instruction.Operand;
                    PushToStack(reference);
                    break;
                }
                case "newarr":
                {
                    ObjectInstance arrayInstance;
                    var reference = CreateObjectInstance(LookUpType(typeof(Array)), out arrayInstance);
                    var elementType = (Type) instruction.Operand;

                    int size = (int)PopFromStack();
                    arrayInstance["Length"] = size;
                    if (elementType.IsValueType)
                    {
                        var array = Array.CreateInstance(elementType, size);
                        arrayInstance["Values"] = array;
                    }
                    else
                    {
                        var array = new Guid[size];
                        arrayInstance["Values"] = array;
                        arrayInstance["ElementType"] = LookUpType(elementType);
                    }

                    PushToStack(reference);
                    break;
                }
                case "nop": break;
                case "pop": PopFromStack(); break;
                case "stelem.i1":
                case "stelem.i2":
                case "stelem.i4":
                {
                    int value = (int)PopFromStack();
                    int index = (int)PopFromStack();
                    var arrayReference = (Guid)PopFromStack();
                    var array = GetFromHeap(arrayReference)["Values"] as int[];
                    array[index] = value;
                    break;
                }
                case "stelem.ref":
                {
                    dynamic value = PopFromStack();
                    int index = (int)PopFromStack();
                    var arrayRef = (Guid)PopFromStack();
                    dynamic arrayValues = GetFromHeap(arrayRef)["Values"];
                    arrayValues[index] = value;
                    break;
                }
                case "stfld":
                {
                    var newFieldValue = PopFromStack();
                    var instanceRef = PopFromStack();
                    var instance = GetFromHeap((Guid)instanceRef);
                    var fieldName = (instruction.Operand as FieldInfo).Name;
                    instance[fieldName] = newFieldValue;
                    break;
                }
                case "stind.ref":
                {
                    var value = PopFromStack();
                    var objectRef = (Guid) PopFromStack();
                    var instance = GetFromHeap(objectRef);
                    instance["Value"] = GetFromHeap((Guid)value)["Value"];
                    break;
                }
                case "stloc.0": PopFromStackToLocal(0); break;
                case "stloc.1": PopFromStackToLocal(1); break;
                case "stloc.2": PopFromStackToLocal(2); break;
                case "stloc.3": PopFromStackToLocal(3); break;
                case "stloc.s": PopFromStackToLocal((byte)instruction.Operand); break;
                case "sub":
                {
                    dynamic op2 = PopFromStack();
                    dynamic op1 = PopFromStack();
                    PushToStack(op1 - op2);
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
                case "callvirt":
                {
                    var callee = LookUpMethod(instruction.Operand as MethodBase);
                    var args = PopArgumentsFromStack(callee);
                    if (!callee.IsStatic)
                    {
                        var instanceRef = PopFromStack();
                        args.Insert(0, instanceRef);

                        if (instruction.Code.Name == "callvirt") // a virtual method can be called non-virtually with call (e.g. base.ToString()) and then it would cause StackOverflow without this check
                        {
                            var method = callee as DotMethod;
                            if (method != null && method.IsVirtual)
                            {
                                var instance = GetFromHeap((Guid)instanceRef);
                                callee = LookUpVirtualMethod(instance.TypeHandler, method);
                            }
                        }
                    }

                    PushToCallStack(callee, args);
                    var nestedInterp = new Interpreter(Runtime);
                    nestedInterp.Execute(callee);
                    break;
                }
                case "newobj":
                {
                    var ctor = LookUpMethod(instruction.Operand as ConstructorInfo);
                    var newObjReference = (ctor as DotConstructor).Invoke(this);

                    var args = PopArgumentsFromStack(ctor);
                    args.Insert(0, newObjReference);

                    PushToCallStack(ctor, args);
                    var nestedInterp = new Interpreter(Runtime);
                    nestedInterp.Execute(ctor);

                    PushToStack(newObjReference);
                    break;
                }
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
                    return (int)instruction.Operand;
                case "brfalse":
                case "brfalse.s":
                    return (int)PopFromStack() == 0 ? (int)instruction.Operand : -1;
                case "brtrue":
                case "brtrue.s":
                    return (int)PopFromStack() == 1 ? (int)instruction.Operand : -1;
                default:
                    throw new NotImplementedException(instruction.Code.Name + " is not implemented.");
            }
        }

        #region Stack, heap and locals manipulation

        #region Call stack

        private void PushToCallStack(DotMethodBase callee, List<object> arguments)
        {
            CheckFullCallStack();
            var newFrame = new StackFrame(CurrentStackFrame.CurrentMethod, callee) { Arguments = arguments };
            Runtime.CallStack.Push(newFrame);
        }

        private void CheckFullCallStack()
        {
            // limited depth of the call stack instead of the stack size, just to make a point to throw a StackOverflow exception
            if (Runtime.CallStack.Count == 25)
                throw new StackOverflowException("There's too many nested calls (25 is the limit).");
        }

        private void UnwindCallStack()
        {
            var frame = Runtime.CallStack.Pop();
            
            var method = frame.CurrentMethod as DotMethod;
            if (method != null && method.ReturnType != typeof(void))
            {
                if (frame.Stack.Count == 0)
                    throw new InvalidOperationException("Can't return a value from a method call, the stack is empty.");

                CurrentStackFrame.Stack.Push(frame.Stack.Pop());
            }
        }

        #endregion

        #region Current method call stack and locals

        private List<object> PopArgumentsFromStack(DotMethodBase method)
        {
            var args = new object[method.ParametersCount];
            for (int i = method.ParametersCount - 1; i >= 0; i--)
            {
                args[i] = PopFromStack();
            }
            return new List<object>(args);
        } 

        internal void PushToStack(object value)
        {
            CurrentStackFrame.Stack.Push(value);
        }

        internal object PopFromStack()
        {
            return CurrentStackFrame.Stack.Pop();
        }

        private void PushLocalToStack(byte index)
        {
            PushToStack(CurrentStackFrame.Locals[index]);
        }

        private void PopFromStackToLocal(byte index)
        {
            CurrentStackFrame.Locals[index] = CurrentStackFrame.Stack.Pop();
        }

        #endregion

        #region GC heap

        internal Guid CreateObjectInstance(DotType typeHandler, out ObjectInstance instance)
        {
            instance = new ObjectInstance(typeHandler);
            return Runtime.Heap.Store(instance);
        }

        internal ObjectInstance GetFromHeap(Guid reference)
        {
            return Runtime.Heap.Get(reference);
        }

        internal Guid CreateRefTypeArray(object[] sourceArray)
        {
            ObjectInstance arrayInstance;
            var arrayRef = CreateObjectInstance(LookUpType(typeof(Array)), out arrayInstance);
            var array = new Guid[sourceArray.Length];
            arrayInstance["Values"] = array;

            var elemType = sourceArray.GetType().GetElementType();
            for (int i = 0; i < sourceArray.Length; i++)
            {
                ObjectInstance elementInstance;
                var elementRef = CreateObjectInstance(LookUpType(elemType), out elementInstance);
                elementInstance["Value"] = sourceArray[i];

                array[i] = elementRef;
            }
            arrayInstance["Length"] = sourceArray.Length;
            return arrayRef;
        }

        #endregion

        #region Method Tables lookups

        internal DotType LookUpType(Type type)
        {
            var moduleName = type.Module.Name.Substring(0, type.Module.Name.Length - 4); // removes a file extension .exe or .dll

            var assembly = Runtime.LoadedAssemblies.FirstOrDefault(a => a.Name == moduleName);
            if (assembly == null)
                throw new NotSupportedException("Not supported assembly " + moduleName);

            var dotType = assembly.Types.FirstOrDefault(t => t.Name == type.Name);
            if (dotType == null)
                throw new NotSupportedException("Not supported type " + type.Name + " in assembly " + moduleName);

            return dotType;
        }

        internal DotMethodBase LookUpMethod(MethodBase mb)
        {
            var type = LookUpType(mb.DeclaringType);
            if (mb.IsConstructor)
            {
                var ctor = type.Constructors.FirstOrDefault(c => c.ParametersTypes.SequenceEqual(mb.GetParameters().Select(p => p.ParameterType)));
                if (ctor == null) throw new NotSupportedException("Not supported constructor " + mb.Name + " in type " + mb.DeclaringType.Name + " in assembly " + mb.Module.Name);
                return ctor;
            }

            var method = type.Methods.FirstOrDefault(m => m.Name == mb.Name && m.ParametersCount == mb.GetParameters().Length);
            if (method == null) throw new NotSupportedException("Not supported method " + mb + " in type " + mb.DeclaringType.Name + " in assembly " + mb.Module.Name);
            return method;
        }

        private DotMethod LookUpVirtualMethod(DotType declaringDerivedType, DotMethod virtualMethod)
        {
            var overridingMethod = declaringDerivedType.Methods.FirstOrDefault(m => m.Name == virtualMethod.Name);
            if (overridingMethod == null)
                throw new ArgumentException("A method does not exist in this type. This should not happen, derived types have always defined all virtual methods.");

            return overridingMethod;
        }

        #endregion
        
        #endregion

    }
}
 