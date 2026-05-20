# -*- coding: utf-8 -*-
"""
分析 Kanzi Studio PluginInterface.dll 的公共 API 面
重点：节点遍历、属性操作、项目结构相关 API
"""
import subprocess
import sys
import os

dll_path = r"C:\Program Files\Rightware\Kanzi 3_9_10_98\Studio\Bin\PluginInterface.dll"

ps_script = rf"""
Add-Type -TypeDefinition @"
    using System;
    using System.Reflection;
    using System.Linq;

    public class DllAnalyzer {{
        public static void Analyze(string path) {{
            try {{
                var asm = Assembly.LoadFrom(@""path"");
                Console.WriteLine($""Assembly: {{asm.GetName().Name}} v{{asm.GetName().Version}}"");
                Console.WriteLine(new string('=', 80));

                var types = asm.GetExportedTypes();
                Console.WriteLine($""Exported Types ({{types.Length}}):"");
                Console.WriteLine();

                foreach (var t in types.OrderBy(t => t.FullName)) {{
                    var tb = t.IsClass ? "[C]" : t.IsInterface ? "[I]" : "[S]";
                    Console.WriteLine($""  {{tb}} {{t.FullName}}"");
                    
                    // 继承链
                    if (t.BaseType != null && t.BaseType != typeof(object)) {{
                        Console.WriteLine($""       inherits: {{t.BaseType.FullName}}"");
                    }}
                    foreach (var iface in t.GetInterfaces()) {{
                        Console.WriteLine($""       impls  : {{iface.Name}}"");
                    }}

                    // 公共方法
                    var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    var relevantMethods = methods.Where(m => !m.Name.StartsWith(""get_"") && !m.Name.StartsWith(""set_""));
                    if (relevantMethods.Any()) {{
                        Console.WriteLine($""       methods:"");
                        foreach (var m in relevantMethods.Take(30)) {{
                            var pars = string.Join("", "", m.GetParameters().Select(p => $""{{p.ParameterType.Name}} {{p.Name}}""));
                            var mod = m.IsStatic ? ""static "" : """";
                            Console.WriteLine($""         {{mod}}{{m.ReturnType.Name}} {{m.Name}}({{pars}})"");
                        }}
                    }}

                    // 公共属性
                    var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    var declProps = props.Where(p => p.DeclaringType == t);
                    if (declProps.Any()) {{
                        Console.WriteLine($""       properties:"");
                        foreach (var p in declProps.Take(30)) {{
                            var mod = p.GetGetMethod()?.IsStatic == true ? ""static "" : """";
                            Console.WriteLine($""         {{mod}}{{p.PropertyType.Name}} {{p.Name}}"");
                        }}
                    }}
                    Console.WriteLine();
                }}
            }}
            catch (Exception ex) {{
                Console.WriteLine($""Error: {{ex.GetType().Name}}: {{ex.Message}}"");
                Console.WriteLine(ex.StackTrace);
            }}
        }}
    }}
"@

# 加载反射助手
Add-Type -AssemblyName System.Reflection

# 调用
[DllAnalyzer]::Analyze('{dll_path}')
"""

# 写临时 PS 脚本并执行
import tempfile
with tempfile.NamedTemporaryFile(mode='w', suffix='.ps1', delete=False, encoding='utf-8') as f:
    f.write(ps_script)
    ps_path = f.name

print(f"[*] 执行 PowerShell 分析: {dll_path}")
result = subprocess.run(
    ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps_path],
    capture_output=True, text=True, timeout=60
)
print(result.stdout)
if result.stderr:
    print("[stderr]", result.stderr)
os.unlink(ps_path)
