using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using TransformsAI.Unity.WebGL.Interop.Internal;
using TransformsAI.Unity.WebGL.Interop.Types;
using static TransformsAI.Unity.WebGL.Interop.Editor.GeneratorCommon;

namespace TransformsAI.Unity.WebGL.Interop.Editor
{
    public static class JsLibGenerator
    {
        public static StringBuilder Builder
        {
            get => GeneratorCommon.Builder;
            set => GeneratorCommon.Builder = value;
        }

        public static string GenerateJsLib()
        {
            Builder = new StringBuilder();
            Builder.AppendLine("// This file was automatically generated by JsLibGenerator.cs");

            using (Builder.Append("mergeInto(LibraryManager.library,").Brace())
            {
                foreach (var method in RuntimeMethods)
                {
                    if (!WriteSpecialMethod(method)) WriteMethod(method);
                    Builder.AppendLine(",");
                }
            }
            Builder.AppendLine(");");
            return Builder.ToString();
        }

        private static void WriteMethod(MethodInfo method)
        {
            var paramList = method.GetParameters();

            var isStandardMethod =
                paramList.Length > 0 && paramList[0].IsOut &&
                paramList[0].ParameterType.GetElementType() == typeof(int);

            if (!isStandardMethod)
                throw new Exception($"Unsupported extern method {method.Name} in {method.DeclaringType}");

            using (WriteMethodHeader(method))
            {
                using (Try)
                {
                    WriteProcessStrings(paramList);
                    Builder.Append("var context = ").AppendContextGetter().AppendLine(";");
                    Builder.Append("var ret = ").AppendMethodForwarding("context", method).AppendLine(";");
                    WriteOutParam(paramList[0], "ret.type");

                    if (method.ReturnType == typeof(string)) WriteStringReturn("ret.value");
                    else Builder.AppendLine("return ret.value;");
                }
                using (Catch("error"))
                {
                    WriteOutParam(paramList[0], "-1");

                    using (Try)
                    {
                        Builder.AppendLine("console.log(error)");
                        Builder.AppendLine("var errString = String(error)");
                        Builder.Append("var strRet = ").AppendContextGetter().AppendLine(".CreateString(errString);");
                        Builder.AppendLine("return strRet.value;");

                    }
                    using (Catch("innerError"))
                    {
                        Builder.AppendLine("return 0;");
                    }
                }
            }
        }

        private static IndentHolder Catch(string errorName) =>
            Builder.Append("catch (").Append(errorName).Append(")").Brace();

        private static IndentHolder Try => Builder.Append("try").Brace();

        private static void WriteOutParam(ParameterInfo parameterInfo, string outVarValue)
        {
            if (!parameterInfo.IsOut) throw new Exception("bad out param");
            Builder.Append("setValue(")
                .Append(parameterInfo.Name)
                .Append(", ")
                .Append(outVarValue)
                .Append(", '")
                .Append(GetEmscriptenType(parameterInfo.ParameterType.GetElementType()))
                .AppendLine("');");

        }

        private static void WriteStringReturn(string retValue)
        {
            Builder.Append("var returnStr  = ").Append(retValue).AppendLine(";");
            Builder.AppendLine("var bufferSize = lengthBytesUTF8(returnStr) + 1;");
            Builder.AppendLine("var strBuffer = _malloc(bufferSize);");
            Builder.AppendLine("stringToUTF8(returnStr, strBuffer, bufferSize);");
            Builder.AppendLine("return strBuffer;");
        }

        private static IndentHolder WriteMethodHeader(MethodBase method) =>
            Builder.Append(method.Name).Append(": function (").AppendParams(method, true).Append(")").Brace();
        private static StringBuilder AppendMethodForwarding(this StringBuilder s, string contextVar, MethodBase method) =>
            s.Append(contextVar).Append('.').Append(method.Name).Append('(').AppendParams(method, false).Append(");");

        private static bool WriteSpecialMethod(MethodInfo method)
        {
            if (method.Name == nameof(RuntimeRaw.InitializeInternal))
            {
                WriteInitializeMethod(method);
                return true;
            }

            return false;
        }

        private static void WriteProcessStrings(ParameterInfo[] paramList)
        {
            foreach (var param in paramList)
            {
                if (param.ParameterType == typeof(string))
                {
                    Builder.Append(param.Name)
                        .Append(" = ")
                        .Append("Pointer_stringify(")
                        .Append(param.Name)
                        .AppendLine(");");
                }
            }
        }

        private static StringBuilder AppendParams(this StringBuilder s, MethodBase method, bool includeOutParams)
        {
            var paramList = method.GetParameters();
            if (!includeOutParams) paramList = paramList.Where(it => !it.IsOut).ToArray();
            var length = paramList.Length;
            if (length == 0) return s;
            s.Append(paramList[0].Name);

            for (var i = 1; i < length; i++)
            {
                s.Append(", ");
                s.Append(paramList[i].Name);
            }
            return s;
        }

        private static StringBuilder AppendContextGetter(this StringBuilder b) =>
            b.Append($@"Module['{InstanceModuleKey}']");


        private static void WriteInitializeMethod(MethodInfo method)
        {
            using (WriteMethodHeader(method))
            {
                var callbackHandler = method.GetParameters()[0];
                var onAcquireCallback = method.GetParameters()[1];
                var onReleaseCallback = method.GetParameters()[2];

                if (!callbackHandler.ParameterType.IsSubclassOf(typeof(Delegate)) ||
                    !onAcquireCallback.ParameterType.IsSubclassOf(typeof(Delegate)) ||
                    !onReleaseCallback.ParameterType.IsSubclassOf(typeof(Delegate)))
                    throw new Exception("Bad Initialize signature");

                var chMethod = callbackHandler.ParameterType.GetMethod("Invoke");
                var oacMethod = onAcquireCallback.ParameterType.GetMethod("Invoke");
                var orcMethod = onReleaseCallback.ParameterType.GetMethod("Invoke");

                Builder.Append("var arrayBuilder = ").AppendArrayBuilderFunction();
                Builder.Append(@"var ch = ").AppendCallbackFunction(callbackHandler.Name, chMethod).AppendLine(";");
                Builder.Append(@"var oac = ").AppendCallbackFunction(onAcquireCallback.Name, oacMethod).AppendLine(";");
                Builder.Append(@"var orc = ").AppendCallbackFunction(onReleaseCallback.Name, orcMethod).AppendLine(";");
                Builder.AppendLine($@"var ctr = Module['{ConstructorModuleKey}'];");
                Builder.AppendLine($@"Module['{InstanceModuleKey}'] = new ctr(arrayBuilder, ch, oac, orc);");
            }
        }

        private static StringBuilder AppendCallbackFunction(this StringBuilder s, string pointerVar, MethodInfo method)
        {
            return s.Append("function(")
                .AppendParams(method, false)
                .Append("){")
                .Append("return Runtime.dynCall('")
                .AppendDyncallSignature(method)
                .Append("', ")
                .Append(pointerVar)
                .Append(", [")
                .AppendParams(method, false)
                .Append("]);}");
        }

        private static StringBuilder AppendDyncallSignature(this StringBuilder s, MethodInfo m)
        {
            s.Append(GetDyncallType(m.ReturnType));

            foreach (var parameter in m.GetParameters())
                s.Append(GetDyncallType(parameter.ParameterType));

            return s;
        }

        private static char GetDyncallType(Type t)
        {
            if (t.IsByRef) return 'i';
            if (t == typeof(void)) return 'v';
            if (!t.IsValueType) return 'i';
            if (t == typeof(float)) return 'f';
            if (t == typeof(double)) return 'd';
            if (Marshal.SizeOf(t) == 4) return 'i';
            if (Marshal.SizeOf(t) == 8) return 'j';
            throw new Exception($"Cannot Marshal type {t}");
        }


        private static string GetEmscriptenType(Type t)
        {
            if (!t.IsValueType) return "i";
            if (t == typeof(byte)) return "i8";
            if (t == typeof(short)) return "i16";
            if (t == typeof(int)) return "i32";
            if (t == typeof(long)) return "i64";
            if (t == typeof(long)) return "i64";
            if (t == typeof(long)) return "i64";
            if (t == typeof(float)) return "float";
            if (t == typeof(double)) return "double";
            throw new Exception($"Cannot Marshal type {t}");
        }

        private static void AppendArrayBuilderFunction(this StringBuilder s)
        {
            using (s.Append("function(pointer, typeCode, length)").Brace())
            {
                using (Builder.Append("switch (typeCode)").Brace())
                {
                    foreach (TypedArrayTypeCode value in Enum.GetValues(typeof(TypedArrayTypeCode)))
                        Builder.Append("case ")
                            .Append((int)value)
                            .Append(": return new ")
                            .Append(value)
                            .AppendLine("(buffer, pointer, length);");
                }
            }
        }
    }
}
