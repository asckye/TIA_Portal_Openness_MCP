using System;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// DescribeBlockLogic 的文本渲染器。盯的是「悄悄丢内容」：
    /// SCL 里的调用、命名常量、绝对地址，以及 LAD 里的 &lt;Call&gt; 块调用，
    /// 原实现都是直接不输出 —— 结果仍然「成功」，只是把逻辑读漏了。
    /// 每组都带反向哨兵：原本就能渲染的形态必须原样通过。
    /// </summary>
    internal static class LadTextRendererTests
    {
        private static string Wrap(string language, string networkSource) => @"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""V21"" />
  <SW.Blocks.FB ID=""0"">
    <AttributeList><Name>T</Name><Number>1</Number><ProgrammingLanguage>" + language + @"</ProgrammingLanguage></AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID=""3"" CompositionName=""CompileUnits"">
        <AttributeList>
          <NetworkSource>" + networkSource + @"</NetworkSource>
          <ProgrammingLanguage>" + language + @"</ProgrammingLanguage>
        </AttributeList>
        <ObjectList />
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>";

        // Every StructuredText shape that a real SCL export nests below the top level.
        private const string SclBody = @"<StructuredText xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4"">
<LineComment UId=""1""><Text> header</Text></LineComment><NewLine UId=""2"" />
<Access Scope=""LocalVariable"" UId=""3""><Symbol UId=""4""><Component Name=""tick"" UId=""5"" /></Symbol></Access>
<Blank UId=""6"" /><Token Text="":="" UId=""7"" /><Blank UId=""8"" />
<Access Scope=""Call"" UId=""9""><CallInfo Name=""TIME_TCK"" BlockType=""FC""><Token Text=""("" UId=""10"" /><Token Text="")"" UId=""11"" /></CallInfo></Access>
<Token Text="";"" UId=""12"" /><NewLine UId=""13"" />
<Access Scope=""LocalVariable"" UId=""14""><Symbol UId=""15""><Component Name=""ms"" UId=""16"" /></Symbol></Access>
<Blank UId=""17"" /><Token Text="":="" UId=""18"" /><Blank UId=""19"" />
<Access Scope=""Call"" UId=""20""><CallInfo Name=""TIME_TO_DINT"" BlockType=""FC""><Token Text=""("" UId=""21"" /><Parameter Name=""IN"" UId=""22""><Access Scope=""LocalVariable"" UId=""23""><Symbol UId=""24""><Component Name=""tick"" UId=""25"" /></Symbol></Access><Blank UId=""26"" /><Token Text=""-"" UId=""27"" /><Blank UId=""28"" /><Access Scope=""LocalVariable"" UId=""29""><Symbol UId=""30""><Component Name=""prev"" UId=""31"" /></Symbol></Access></Parameter><Token Text="")"" UId=""32"" /></CallInfo></Access>
<Token Text="";"" UId=""33"" /><NewLine UId=""34"" />
<Access Scope=""LocalVariable"" UId=""35""><Symbol UId=""36""><Component Name=""lim"" UId=""37"" /></Symbol></Access>
<Blank UId=""38"" /><Token Text="":="" UId=""39"" /><Blank UId=""40"" />
<Access Scope=""Call"" UId=""41""><CallInfo Name=""LIMIT"" BlockType=""FC""><Token Text=""("" UId=""42"" /><Parameter Name=""MN"" UId=""43""><Token Text="":="" UId=""44"" /><Blank UId=""45"" /><Access Scope=""LiteralConstant"" UId=""46""><Constant UId=""47""><ConstantValue UId=""48"">0</ConstantValue></Constant></Access></Parameter><Token Text="","" UId=""49"" /><Blank UId=""50"" /><Parameter Name=""MX"" UId=""51""><Token Text="":="" UId=""52"" /><Blank UId=""53"" /><Access Scope=""LocalConstant"" UId=""54""><Constant Name=""MAX_MS"" UId=""55"" /></Access></Parameter><Token Text="")"" UId=""56"" /></CallInfo></Access>
<Token Text="";"" UId=""57"" /><NewLine UId=""58"" />
<Access Scope=""Call"" UId=""59""><CallInfo Name=""TON"" BlockType=""FB""><Instance Scope=""LocalVariable"" UId=""60""><Component Name=""statTimer"" UId=""61"" /></Instance><Token Text=""("" UId=""62"" /><Parameter Name=""IN"" UId=""63""><Token Text="":="" UId=""64"" /><Blank UId=""65"" /><Access Scope=""GlobalVariable"" UId=""66""><Symbol UId=""67""><Component Name=""DB"" UId=""68"" /><Component Name=""run"" UId=""69"" /></Symbol></Access></Parameter><Token Text="","" UId=""70"" /><Blank UId=""71"" /><Parameter Name=""PT"" UId=""72""><Token Text="":="" UId=""73"" /><Blank UId=""74"" /><Access Scope=""TypedConstant"" UId=""75""><Constant UId=""76""><ConstantType>Time</ConstantType><ConstantValue UId=""77"">T#1S</ConstantValue></Constant></Access></Parameter><Token Text="")"" UId=""78"" /></CallInfo></Access>
<Token Text="";"" UId=""79"" /><NewLine UId=""80"" />
<Access Scope=""LocalVariable"" UId=""81""><Symbol UId=""82""><Component Name=""arr"" UId=""83""><Access Scope=""LocalVariable"" UId=""84""><Symbol UId=""85""><Component Name=""i"" UId=""86"" /></Symbol></Access></Component></Symbol></Access>
<Blank UId=""87"" /><Token Text="":="" UId=""88"" /><Blank UId=""89"" />
<Access Scope=""Address"" UId=""90""><Address Area=""Input"" Type=""Bool"" BitOffset=""11"" UId=""91"" /></Access>
<Token Text="";"" UId=""92"" /><NewLine UId=""93"" />
<Comment UId=""94""><Text>block</Text></Comment><NewLine UId=""95"" />
</StructuredText>";

        // FB call box with a user-named Bool input driven by contact logic, an operand-bound input, an operand-bound
        // output, EN through a NOT, and a Bool output feeding a coil.
        private const string LadCallBody = @"<FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"">
  <Parts>
    <Access Scope=""GlobalVariable"" UId=""21""><Symbol><Component Name=""Start"" /></Symbol></Access>
    <Access Scope=""GlobalVariable"" UId=""22""><Symbol><Component Name=""Auto"" /></Symbol></Access>
    <Access Scope=""LocalVariable"" UId=""23""><Symbol><Component Name=""setpoint"" /></Symbol></Access>
    <Access Scope=""GlobalVariable"" UId=""24""><Symbol><Component Name=""Q1"" /></Symbol></Access>
    <Access Scope=""GlobalVariable"" UId=""25""><Symbol><Component Name=""BusyLamp"" /></Symbol></Access>
    <Part Name=""Contact"" UId=""26"" />
    <Part Name=""Not"" UId=""27"" />
    <Part Name=""Contact"" UId=""28"" />
    <Call UId=""29"">
      <CallInfo Name=""FB_Motor"" BlockType=""FB"">
        <Instance Scope=""GlobalVariable"" UId=""30""><Component Name=""FB_Motor_DB"" /></Instance>
        <Parameter Name=""start"" Section=""Input"" Type=""Bool"" />
        <Parameter Name=""sp"" Section=""Input"" Type=""Real"" />
        <Parameter Name=""done"" Section=""Output"" Type=""Bool"" />
        <Parameter Name=""busy"" Section=""Output"" Type=""Bool"" />
      </CallInfo>
    </Call>
    <Part Name=""Coil"" UId=""31"" />
  </Parts>
  <Wires>
    <Wire UId=""40""><Powerrail /><NameCon UId=""26"" Name=""in"" /><NameCon UId=""28"" Name=""in"" /></Wire>
    <Wire UId=""41""><IdentCon UId=""21"" /><NameCon UId=""26"" Name=""operand"" /></Wire>
    <Wire UId=""42""><NameCon UId=""26"" Name=""out"" /><NameCon UId=""27"" Name=""in"" /></Wire>
    <Wire UId=""43""><NameCon UId=""27"" Name=""out"" /><NameCon UId=""29"" Name=""en"" /></Wire>
    <Wire UId=""44""><IdentCon UId=""22"" /><NameCon UId=""28"" Name=""operand"" /></Wire>
    <Wire UId=""45""><NameCon UId=""28"" Name=""out"" /><NameCon UId=""29"" Name=""start"" /></Wire>
    <Wire UId=""46""><IdentCon UId=""23"" /><NameCon UId=""29"" Name=""sp"" /></Wire>
    <Wire UId=""47""><NameCon UId=""29"" Name=""done"" /><IdentCon UId=""24"" /></Wire>
    <Wire UId=""48""><NameCon UId=""29"" Name=""busy"" /><NameCon UId=""31"" Name=""in"" /></Wire>
    <Wire UId=""49""><IdentCon UId=""25"" /><NameCon UId=""31"" Name=""operand"" /></Wire>
  </Wires>
</FlgNet>";

        // Sentinel: the shapes that already rendered before the fix (contact wired to FALSE, MOVE box).
        private const string LadLegacyBody = @"<FlgNet xmlns=""http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"">
  <Parts>
    <Access Scope=""LiteralConstant"" UId=""21""><Constant><ConstantType>Bool</ConstantType><ConstantValue>FALSE</ConstantValue></Constant></Access>
    <Access Scope=""LiteralConstant"" UId=""22""><Constant><ConstantType>Int</ConstantType><ConstantValue>42</ConstantValue></Constant></Access>
    <Access Scope=""LocalVariable"" UId=""23""><Symbol><Component Name=""DST"" /></Symbol></Access>
    <Access Scope=""GlobalVariable"" UId=""24""><Symbol><Component Name=""Lamp"" /></Symbol></Access>
    <Part Name=""Contact"" UId=""25"" />
    <Part Name=""Move"" UId=""26"" />
    <Part Name=""Coil"" UId=""27"" />
  </Parts>
  <Wires>
    <Wire UId=""30""><Powerrail /><NameCon UId=""25"" Name=""in"" /></Wire>
    <Wire UId=""31""><IdentCon UId=""21"" /><NameCon UId=""25"" Name=""operand"" /></Wire>
    <Wire UId=""32""><NameCon UId=""25"" Name=""out"" /><NameCon UId=""26"" Name=""en"" /></Wire>
    <Wire UId=""33""><IdentCon UId=""22"" /><NameCon UId=""26"" Name=""in"" /></Wire>
    <Wire UId=""34""><NameCon UId=""26"" Name=""out1"" /><IdentCon UId=""23"" /></Wire>
    <Wire UId=""35""><NameCon UId=""26"" Name=""eno"" /><NameCon UId=""27"" Name=""in"" /></Wire>
    <Wire UId=""36""><IdentCon UId=""24"" /><NameCon UId=""27"" Name=""operand"" /></Wire>
  </Wires>
</FlgNet>";

        public static void Run(Action<bool, string> check)
        {
            var scl = LadTextRenderer.Render(Wrap("SCL", SclBody));
            check(scl.Contains("#tick := TIME_TCK();"), "SCL: 无参系统调用完整保留（原实现输出 ':= ;'）  <- " + scl);
            check(scl.Contains("#ms := TIME_TO_DINT(#tick - #prev);"), "SCL: 位置参数里的表达式按 token 顺序递归渲染，不再压扁成 a.b");
            check(scl.Contains("#lim := LIMIT(MN := 0, MX := #MAX_MS);"), "SCL: 命名参数带参数名；LocalConstant 命名常量输出 #NAME");
            check(scl.Contains("#statTimer⟨TON⟩(IN := \"DB\".run, PT := T#1S);"), "SCL: 多重实例调用显示实例名并标注 FB 类型；全局 DB 成员只给首段加引号；TypedConstant 保值");
            check(scl.Contains("#arr[#i] := %I1.3;"), "SCL: 数组下标 Access 与绝对地址");
            check(scl.Contains("// header") && scl.Contains("(*block*)"), "SCL: 行注释 // 与块注释 (* *) 分开");
            check(!scl.Contains(":= ;") && !scl.Contains("( ;"), "[哨兵] SCL 输出里不再有空赋值/空实参");

            var lad = LadTextRenderer.Render(Wrap("LAD", LadCallBody));
            check(lad.Contains("当 [NOT(\"Start\")] 时: CALL \"FB_Motor\"[\"FB_Motor_DB\"]("), "LAD: <Call> 被识别为输出元素，EN 经 NOT 回溯  <- " + lad);
            check(lad.Contains("start=⟨\"Auto\"⟩"), "LAD: 按 Parameter Section=Input 识别用户命名的 Bool 输入引脚并回溯触点链");
            check(lad.Contains("sp=#setpoint") && lad.Contains("done=\"Q1\""), "LAD: 调用框的操作数绑定（输入/输出）");
            check(lad.Contains("\"BusyLamp\" ( )  ⇐  NOT(\"Start\") · CALL \"FB_Motor\"[\"FB_Motor_DB\"].busy"), "LAD: 块 Bool 输出驱动线圈时内联为 CALL….pin（原实现整段网络退化成触点清单）");
            check(!lad.Contains("   · Contact"), "[哨兵] 有调用框的网络不再落入「逐个列零件」的兜底输出");

            var legacy = LadTextRenderer.Render(Wrap("LAD", LadLegacyBody));
            check(legacy.Contains("当 [FALSE ⟨恒断·禁用本行⟩] 时: MOVE 42 → #DST"), "[反向哨兵] 常量触点标注与 MOVE 框照旧  <- " + legacy);
            check(legacy.Contains("\"Lamp\" ( )  ⇐  FALSE ⟨恒断·禁用本行⟩ · Move("), "[反向哨兵] 经 ENO 串联到线圈的链照旧");
        }
    }
}
