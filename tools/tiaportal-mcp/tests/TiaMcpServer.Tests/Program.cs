using System;
using System.Collections.Generic;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 最小离线自检套件的入口。不连 TIA Portal，不碰注册表，跑得起 dotnet 就跑得起它。
    ///
    /// 它盯的是一类特定的缺陷：**从上线起就没生效过的检查**。
    /// 这类东西的共同点是「不响的报警器和没装的报警器长得一模一样」——
    /// 靠人读代码发现不了，只有会失败的用例盯得住。所以每组用例里都放了反向哨兵：
    /// 正常输入必须照旧通过，否则说明这次是把功能改坏了而不是把检查修好了。
    ///
    /// ⚠️ 用 `dotnet run` 驱动，别用 `dotnet test`（见 csproj 里的说明）。
    /// </summary>
    internal static class Program
    {
        private static int _pass;
        private static int _fail;
        private static int _skip;

        private static void Check(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
            }
            else
            {
                _fail++;
                Console.WriteLine("  FAIL: " + what);
            }
        }

        /// <summary>
        /// 用例依赖仓外/仓内布局才拿得到的东西（如引擎源码）。拿不到就明确跳过并计数：
        /// 「少跑了一批」和「全过了」必须在汇总行里长得不一样，否则换台机器跑
        /// 就是个永远不响的报警器。
        /// </summary>
        private static void Skip(string what, string why)
        {
            _skip++;
            Console.WriteLine("  SKIP: " + what + "  <- " + why);
        }

        private static int Main()
        {
            PlcTypeGroupCreationTests.Run(Check);
            EngineeringOperationsTests.Run(Check);
            EngineeringObjectAddressTests.Run(Check);
            NativeFileOutputTests.Run(Check);
            ExtendedEngineeringTests.Run(Check);
            MigrationReadTests.Run(Check);
            EngineeringDefectTests.Run(Check);
            HmiInspectionTests.Run(Check);
            HmiSnapshotSafetyTests.Run(Check);
            UnifiedGlobalScriptEditTests.Run(Check);
            GraphicSelectionTests.Run(Check);
            RuntimeSettingsTests.Run(Check);
            SoftwareContainerLookupTests.Run(Check);
            PlcListingReadTests.Run(Check);
            UnifiedMultilingualTextTests.Run(Check);
            Console.WriteLine("== 「执行 JSON 检查」不许是复述已知事实的同义反复 ==");
            HmiTemplateLayoutExecutionCheckTests.Run(Check);

            Console.WriteLine("== .s7res 的 en-US 扫描（原实现对每个真实文件都抛异常又被吞掉）==");
            RunS7ResScannerTests();

            Console.WriteLine("== 参数诊断 + 大响应寄存分页（坏了也悄无声息的两块）==");
            ExportsAndArgDiagnosticsTests.Run(Check);

            Console.WriteLine("== Unified JS 脚本的 SyntaxCheck 默认关闭 + 进程级致命错不许被吞（issue #36）==");
            UnifiedScriptSyntaxCheckTests.Run(Check, Skip);

            Console.WriteLine("== HMI 画面列表和按名称查找必须覆盖所有子文件夹 ==");
            HmiScreenTraversalTests.Run(Check);

            Console.WriteLine("== 下载/上载提示按真实形态应答；UserManagementDownload 不再被当复选框 ==");
            DownloadPromptPolicyTests.Run(Check);

            Console.WriteLine("== 新工具族纯逻辑：块服务、硬件服务、工程安全、Unified UI、Motion/ProDiag/经典 HMI、离线分析 ==");
            PlcBlockServicesTests.Run(Check);
            HardwareServicesTests.Run(Check);
            ProjectSecurityTests.Run(Check);
            UnifiedUiModelTests.Run(Check);
            MotionProDiagClassicHmiTests.Run(Check);
            OfflineAnalysisTests.Run(Check);

            Console.WriteLine("== 运行时通道：S7 Web 服务器 API 与 Unified Open Pipe 的请求构造、响应解析与拒绝路径 ==");
            RuntimeChannelsTests.Run(Check);

            Console.WriteLine("== 2.7.19 新增：离线文档/SCL 预检、PLCSIM Advanced 纯逻辑、AML 生成 ==");
            PlcDocumentationTests.Run(Check);
            PlcSimAdvancedTests.Run(Check);
            HardwareAmlTests.Run(Check);

            Console.WriteLine("== DescribeBlockLogic 文本渲染：SCL 调用/命名常量/地址与 LAD <Call> 不许悄悄丢失 ==");
            LadTextRendererTests.Run(Check);

            Console.WriteLine("== Safety 工具族纯逻辑：动作门控、propertiesJson 四路拆分、GlobalSettings、打印件参数、签名行 ==");
            SafetyLogicTests.Run(Check);

            Console.WriteLine("== Unified 画面对象：类型目录/解析、属性 schema、多语言拆分、嵌套写入 ==");
            UnifiedScreenItemTests.Run(Check);

            Console.WriteLine("== Unified 原生交换：变量 WinCC ML / 脚本模块 / OPC UA 报警 xml 的目录、文件与结果核对 ==");
            UnifiedExchangeTests.Run(Check);

            Console.WriteLine("== 硬件网络深层纯逻辑：IO 系统/同步域/MRP/传输区/通道/地址/设备用户/端口互连的参数门控、枚举目录、权限标志 ==");
            HardwareNetworkTests.Run(Check);

            Console.WriteLine("== 库深层纯逻辑：选择/范围解析、模式目录、同步/类型/比较请求门控、GUID 与归档名；HmiReadSafety 释放对象分类 ==");
            LibraryDeepTests.Run(Check);

            Console.WriteLine("== 安全/UMC 深层纯逻辑：syslog（工程/PLC）请求门控、密码策略目标与范围、UMC 用户/组/服务器请求与凭据规则、证书模板与 SAN、匿名用户动作 ==");
            SecurityDeepTests.Run(Check);

            Console.WriteLine("== Base 收尾纯逻辑：硬件工具、设备服务对象（Web 应用 / 遥控数据点 / 动态证书）、对象选择、事务调用清单、UMAC / 在线凭据、R/H 目标 ==");
            BaseLeftoversTests.Run(Check);

            Console.WriteLine("== Step7 软件单元深层纯逻辑：单元 / 安全单元请求、命名值类型与 UDT 文档请求、指纹、块写保护状态机、工程编译设置、目录快照 ==");
            SoftwareUnitDeepTests.Run(Check);

            Console.WriteLine("== Step7 收尾纯逻辑：外部源（文件 / 母本 / 用户组 / 生成块）、系统组与常量请求、报警文本列表 XLSX、监控 / 强制表条目、ProDiag 门控 ==");
            Step7LeftoversTests.Run(Check);

            Console.WriteLine("== 工艺对象映射纯逻辑：Connect(Channel) 目标与各接口的重载目录 ==");
            TechnologyMappingTests.Run(Check);

            Console.WriteLine("== 经典 WinCC 文件夹纯逻辑：画面树、弹出 / 模板 / 滑入 / 总览 / 全局元素对象请求、五类文件夹请求、多语言图形请求 ==");
            ClassicHmiFoldersTests.Run(Check);

            Console.WriteLine("== SiVArc 纯逻辑：规则族、文件夹 / 表请求、规则 / 组请求（属性、引用、设备列）、块定义请求、表达式 / 布局请求、生成选项 ==");
            SivarcTests.Run(Check);

            Console.WriteLine("== Startdrive 纯逻辑：驱动对象选择器、参数选择器与 BICO 值、报文请求、驱动功能请求、安全 / 工艺扩展 / 硬件模块 / 验收测试 / 在线门 ==");
            StartdriveTests.Run(Check);

            Console.WriteLine("== DCC 纯逻辑：图表 / 块 / 引脚 / 图表接口 / 分区 / DCB 库请求、可写属性目录、导入选项与文件门 ==");
            DccTests.Run(Check);

            Console.WriteLine("== SafetyValidation 纯逻辑：激活测试组路径、激活测试 / 组 / 安全功能 / 条件请求、导出导入选项与条件值 ==");
            SafetyValidationTests.Run(Check);

            Console.WriteLine("== Test Suite 纯逻辑：类别 / 种类、加载选项语法、交换 / 执行 / 管理请求与样式指南作用域条目 ==");
            TestSuiteTests.Run(Check);

            Console.WriteLine("== Teamcenter 纯逻辑：连接 / 数据集 / 工作流请求、条目与修订详情、自定义属性 ==");
            TeamcenterTests.Run(Check);

            Console.WriteLine("== CFC 纯逻辑：完整 / 选择导出、导入、指令数据与图表密码请求 ==");
            CfcTests.Run(Check);
            PlcTagEditingTests.Run(Check);

            Console.WriteLine(_fail == 0
                ? $"{_pass} passed, {_fail} failed, {_skip} skipped."
                : $"{_pass} passed, {_fail} failed, {_skip} skipped.  <<< 有失败");
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// .s7res 是 YAML，不是 XML。原实现拿 XML 解析器去读，对**每一个真实文件**都抛异常，
        /// 异常又被外面的 catch 吞掉 —— 于是「没有缺失的 en-US 条目」这个结论，
        /// 是在一次都没真正扫过的情况下得出的。这里用真实形态的行喂它。
        /// </summary>
        private static void RunS7ResScannerTests()
        {
            // 真实形态：MultiLingualTexts 容器 + 「- id: MLC_xxx」列表项 + 各语言行。
            var missing = new List<string>
            {
                "MultiLingualTexts:",
                "  - id: MLC_Comment",
                "    de-DE: 'Start'",
                "  - id: MLC_Title",
                "    en-US: 'Stop'",
            };
            var ids = TiaMcpServer.ModelContextProtocol.S7ResScanner.GetMissingEnUsIdsFromLines(missing);
            Check(ids.Contains("MLC_Comment"), "缺 en-US 的条目要被点名（MLC_Comment）");
            Check(!ids.Contains("MLC_Title"), "有 en-US 的条目不许被误报（MLC_Title）");

            // 反向哨兵：全都有 en-US 时必须一条都不报。
            // 少了这条，「永远返回空列表」的坏实现也能通过上面那两条里的第二条。
            var complete = new List<string>
            {
                "MultiLingualTexts:",
                "  - id: MLC_Comment",
                "    en-US: 'Start'",
                "  - id: MLC_Title",
                "    en-US: 'Stop'",
            };
            Check(TiaMcpServer.ModelContextProtocol.S7ResScanner.GetMissingEnUsIdsFromLines(complete).Count == 0,
                "[反向哨兵] 全都有 en-US 时不许报缺失");

            // [哨兵] 空输入不许崩：预检坏掉本身就该看得见，但不该把调用方一起带走。
            Check(TiaMcpServer.ModelContextProtocol.S7ResScanner.GetMissingEnUsIdsFromLines(new List<string>()).Count == 0,
                "[哨兵] 空输入返回空列表且不抛");
        }
    }
}
