# -*- coding: utf-8 -*-
"""
使用 System.Reflection.Metadata 读取 PluginInterface.dll 的公共 API 面
无需加载依赖程序集，纯元数据读取
"""
import subprocess
import sys
import os
import tempfile

dll_path = r"C:\Users\WTY\WorkBuddy\kanziMcpServer\pluginInterface\kanzi3.9.10\PluginInterface.dll"

ps_script = rf"""
# 使用 Reflection.Metadata 读取程序集元数据（无需加载依赖）
Add-Type -AssemblyName System.Collections
Add-Type -TypeDefinition @"
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection.Metadata;
    using System.Reflection.PortableExecutable;
    using System.Reflection;

    public class MetadataReaderHelper {{
        public static void ReadAssembly(string path) {{
            using (var stream = File.OpenRead(path)) {{
                using (var peReader = new PEReader(stream)) {{
                    var metadataReader = peReader.GetMetadataReader();
                    
                    Console.WriteLine("=== TYPES ===");
                    foreach (var handle in metadataReader.TypeDefinitions) {{
                        var typeDef = metadataReader.GetTypeDefiniton(handle);
                        var name = metadataReader.GetString(typeDef.Name);
                        var ns = metadataReader.GetString(typeDef.Namespace);
                        var fullName = ns + "." + name;
                        
                        // 跳过内部类型
                        if (name.StartsWith("<") || name.Contains(">")) continue;
                        
                        var flags = typeDef.Attributes;
                        bool isPublic = (flags & TypeAttributes.Public) != 0;
                        bool isInterface = (flags & TypeAttributes.Interface) != 0;
                        bool isAbstract = (flags & TypeAttributes.Abstract) != 0;
                        bool isSealed = (flags & TypeAttributes.Sealed) != 0;
                        
                        if (!isPublic) continue;
                        
                        string kind = isInterface ? "[I]" : (isAbstract ? "[A]" : (isSealed ? "[S]" : "[C]"));
                        Console.WriteLine($"  {{kind}} {{fullName}}");
                        
                        // 基类
                        if (typeDef.BaseType.IsNil == false) {{
                            try {{
                                var baseTypeHandle = (TypeDefinitionHandle)typeDef.BaseType.Handle;
                                var baseTypeDef = metadataReader.GetTypeDefiniton(baseTypeHandle);
                                var baseName = metadataReader.GetString(baseTypeDef.Name);
                                var baseNs = metadataReader.GetString(baseTypeDef.Namespace);
                                Console.WriteLine($"       inherits: {{baseNs}}.{{baseName}}");
                            }} catch {{}}
                        }}
                        
                        // 方法
                        var methods = new List<string>();
                        foreach (var methodHandle in typeDef.GetMethods()) {{
                            var methodDef = metadataReader.GetMethodDefinition(methodHandle);
                            var mName = metadataReader.GetString(methodDef.Name);
                            // 跳过属性访问器、事件注册方法
                            if (mName.StartsWith("get_") || mName.StartsWith("set_") || 
                                mName.StartsWith("add_") || mName.StartsWith("remove_") ||
                                mName.StartsWith("put_")) continue;
                            if (mName.StartsWith("<")) continue;
                            
                            var access = methodDef.Attributes & MethodAttributes.MemberAccessMask;
                            if (access != MethodAttributes.Public && access != MethodAttributes.FamORAssem) continue;
                            
                            // 返回类型
                            try {{
                                var sigReader = metadataReader.GetBlobReader(methodDef.Signature);
                                // 简略：只输出方法名
                                string mod = (methodDef.Attributes & MethodAttributes.Static) != 0 ? "static " : "";
                                methods.Add($"         {{mod}}... {{mName}}(...)");
                            }} catch {{}}
                        }}
                        
                        // 属性
                        foreach (var propHandle in typeDef.GetProperties()) {{
                            var propDef = metadataReader.GetPropertyDefinition(propHandle);
                            var pName = metadataReader.GetString(propDef.Name);
                            string mod = "";
                            try {{
                                var getter = propDef.GetMethod;
                                if (!getter.IsNil) {{
                                    var methodDef = metadataReader.GetMethodDefinition(getter);
                                    if ((methodDef.Attributes & MethodAttributes.Static) != 0) mod = "static ";
                                }}
                            }} catch {{}}
                            methods.Add($"         {{mod}}... {{pName}}");
                        }}
                        
                        // 只输出前 30 个成员
                        int count = 0;
                        foreach (var m in methods) {{
                            if (count++ < 30) Console.WriteLine(m);
                        }}
                        if (methods.Count > 30) Console.WriteLine($"         ... ({{methods.Count - 30}} more)");
                        Console.WriteLine();
                    }}
                }}
            }}
        }}
    }}
"@

[Melenie.Reflection.MetadataReaderHelper]::ReadAssembly('{dll_path}')
"""

with tempfile.NamedTemporaryFile(mode='w', suffix='.ps1', delete=False, encoding='utf-8') as f:
    f.write(ps_script)
    ps_path = f.name

print(f"[*] 分析 DLL: {dll_path}")
result = subprocess.run(
    ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps_path],
    capture_output=True, text=True, timeout=60
)
print(result.stdout)
if result.stderr:
    print("[stderr]", result.stderr[:2000])
os.unlink(ps_path)
