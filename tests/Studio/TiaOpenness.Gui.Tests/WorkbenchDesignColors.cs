using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Xunit;

namespace TiaOpenness.Gui.Tests;

internal static class WorkbenchDesignColors
{
    // WPF ARGB equivalents of the final design tokens. Column order follows the five palette files.
    private static readonly string[] Themes = ["Light", "Dark", "Light.Petrol", "Dark.Petrol", "HighContrast"];
    private const string Values = """
        bg #F8FAFD #141B26 #F8FAFD #141B26 #000000
        card #FFFFFF #1B2332 #FFFFFF #1B2332 #000000
        cardSoft #F3F6FA #101620 #F3F6FA #101620 #000000
        cardSel #160F172A #1AE2E8F0 #160F172A #1AE2E8F0 #333300
        cardBorder #170F172A #2194A3B8 #170F172A #2194A3B8 #FFFFFF
        divider #170F172A #2194A3B8 #170F172A #2194A3B8 #FFFFFF
        input #EAEFF6 #232E40 #EAEFF6 #232E40 #000000
        inputBorder #290F172A #3D94A3B8 #290F172A #3D94A3B8 #FFFFFF
        pill #EAEFF6 #232E40 #EAEFF6 #232E40 #000000
        pillHover #0E0F172A #0FE2E8F0 #0E0F172A #0FE2E8F0 #1A1A1A
        destructiveText #DC2626 #FB7185 #DC2626 #FB7185 #FF6B6B
        onDanger #FFFFFF #FFFFFF #FFFFFF #FFFFFF #FFFFFF
        menuBg #FFFFFF #1B2332 #FFFFFF #1B2332 #000000
        text #0F172A #F1F5F9 #0F172A #F1F5F9 #FFFFFF
        textMuted #475569 #94A3B8 #475569 #94A3B8 #FFFFFF
        textFaint #64748B #7A8CA3 #64748B #7A8CA3 #D6D6D6
        checkBorder #290F172A #3D94A3B8 #290F172A #3D94A3B8 #FFFFFF
        accent #1D4ED8 #60A5FA #0B656E #2DD4BF #FFFF00
        onAccent #FFFFFF #FFFFFF #FFFFFF #FFFFFF #000000
        primaryBg #2563EB #2563EB #0E7C86 #0E7C86 #FFFF00
        onPrimary #FFFFFF #FFFFFF #FFFFFF #FFFFFF #000000
        warn #B45309 #FBBF24 #B45309 #FBBF24 #FFFF00
        warnBg #1CB45309 #21FBBF24 #1CB45309 #21FBBF24 #000000
        ok #15803D #34D399 #15803D #34D399 #3FF23F
        okBg #1A15803D #1F34D399 #1A15803D #1F34D399 #000000
        noteBg #1A2563EB #293B82F6 #1A0E7C86 #2914B8A6 #000000
        noteBorder #472563EB #5760A5FA #470E7C86 #572DD4BF #FFFF00
        noteAccent #1D4ED8 #60A5FA #0B656E #2DD4BF #FFFF00
        codeBg #F6F8FC #0C1017 #F6F8FC #0C1017 #000000
        codeText #0F172A #F1F5F9 #0F172A #F1F5F9 #FFFFFF
        diffGreen #15803D #6EE7B7 #15803D #6EE7B7 #3FF23F
        diffRed #B91C1C #FDA4AF #B91C1C #FDA4AF #FF9999
        primaryText #FFFFFF #FFFFFF #FFFFFF #FFFFFF #000000
        warningText #FFFFFF #0B0F15 #FFFFFF #0B0F15 #000000
        countWarningBg #1CB45309 #21FBBF24 #1CB45309 #21FBBF24 #000000
        focusOuter #2563EB #2563EB #0E7C86 #0E7C86 #FFFF00
        focusInner #FFFFFF #0B0F15 #FFFFFF #0B0F15 #000000
        selectedText #0F172A #F1F5F9 #0F172A #F1F5F9 #FFFFFF
        hoverText #0F172A #F1F5F9 #0F172A #F1F5F9 #FFFFFF
        Ant.colorPrimary #2563EB #2563EB #0E7C86 #0E7C86 #FFFF00
        Ant.colorPrimaryHover #1D4ED8 #1D4ED8 #0B656E #0B656E #FFE600
        Ant.colorPrimaryActive #1D4ED8 #1D4ED8 #0B656E #0B656E #FFE600
        Ant.colorLinkHover #1D4ED8 #60A5FA #0B656E #2DD4BF #FFFF00
        Ant.controlItemBgActiveHover #160F172A #1AE2E8F0 #160F172A #1AE2E8F0 #333300
        Ant.colorWhite #FFFFFF #FFFFFF #FFFFFF #FFFFFF #000000
        Ant.colorBorder #170F172A #2194A3B8 #170F172A #2194A3B8 #FFFFFF
        Ant.colorError #DC2626 #FB7185 #DC2626 #FB7185 #FF6B6B
        Ant.colorErrorBg #17DC2626 #21FB7185 #17DC2626 #21FB7185 #000000
        Ant.colorErrorBorder #DC2626 #FB7185 #DC2626 #FB7185 #FF6B6B
        Ant.colorErrorSolid #DC2626 #E11D48 #DC2626 #E11D48 #FF0000
        Ant.colorErrorSolidHover #EE2929 #F31F4E #EE2929 #F31F4E #FF0000
        Ant.colorErrorSolidActive #EE2929 #F31F4E #EE2929 #F31F4E #FF0000
        Ant.colorWarningBorder #B45309 #FBBF24 #B45309 #FBBF24 #FFFF00
        Ant.colorSuccessBorder #15803D #34D399 #15803D #34D399 #3FF23F
        Ant.colorFillSecondary #EAEFF6 #232E40 #EAEFF6 #232E40 #000000
        Ant.colorBgContainerDisabled #F3F6FA #101620 #F3F6FA #101620 #000000
        Ant.colorTextDisabled #64748B #7A8CA3 #64748B #7A8CA3 #D6D6D6
        Ant.colorBgMask #4D0F172A #AD070A0F #4D0F172A #AD070A0F #D1000000
        Ant.colorBgSpotlight #FFFFFF #1B2332 #FFFFFF #1B2332 #000000
        Ant.colorTextLightSolid #0F172A #F1F5F9 #0F172A #F1F5F9 #FFFFFF
        Ant.boxShadowKey #0F0F172A #59000000 #0F0F172A #59000000 Transparent
        Ant.boxShadowUmbra #0A0F172A #42000000 #0A0F172A #42000000 Transparent
        Ant.boxShadowAmbient Transparent Transparent Transparent Transparent Transparent
        window #FFFFFF #1B2332 #FFFFFF #1B2332 #000000
        sidebar #F3F6FA #101620 #F3F6FA #101620 #000000
        hcOutline #00000000 #00000000 #00000000 #00000000 #FFFFFF
        titlebar #E9EEF5 #0B0F15 #E9EEF5 #0B0F15 #000000
        rail #E9EEF5 #0B0F15 #E9EEF5 #0B0F15 #000000
        statusBar #E9EEF5 #0B0F15 #E9EEF5 #0B0F15 #000000
        accentSoft #1A2563EB #293B82F6 #1A0E7C86 #2914B8A6 #000000
        accentRing #472563EB #5760A5FA #470E7C86 #572DD4BF #FFFF00
        blkObBg #1C7C3AED #29A78BFA #1C7C3AED #29A78BFA #000000
        blkObFg #6D28D9 #C4B5FD #6D28D9 #C4B5FD #FFFFFF
        blkFbBg #1C2563EB #2960A5FA #1C2563EB #2960A5FA #000000
        blkFbFg #1D4ED8 #93C5FD #1D4ED8 #93C5FD #FFFFFF
        blkFcBg #1C059669 #2634D399 #1C059669 #2634D399 #000000
        blkFcFg #047857 #6EE7B7 #047857 #6EE7B7 #FFFFFF
        blkDbBg #1C0891B2 #2622D3EE #1C0891B2 #2622D3EE #000000
        blkDbFg #0E7490 #67E8F9 #0E7490 #67E8F9 #FFFFFF
        blkFsBg #1FD97706 #29FBBF24 #1FD97706 #29FBBF24 #000000
        blkFsFg #B45309 #FDE68A #B45309 #FDE68A #FFFF00
        syntaxKeyword #2563EB #7BB4FF #2563EB #7BB4FF #1AEBFF
        syntaxComment #64748B #7A8CA3 #64748B #7A8CA3 #D6D6D6
        syntaxString #047857 #6EE7B7 #047857 #6EE7B7 #3FF23F
        syntaxNumber #B45309 #FDE68A #B45309 #FDE68A #FFFF00
        syntaxVariable #7C3AED #C4B5FD #7C3AED #C4B5FD #FF99FF
        diffAddBg #1716A34A #1C34D399 #1716A34A #1C34D399 #002800
        diffDelBg #14DC2626 #1CFB7185 #14DC2626 #1CFB7185 #3A0000
        clientClaudeBg #21D97757 #21D97757 #21D97757 #21D97757 #21D97757
        clientClaudeFg #D97757 #D97757 #D97757 #D97757 #D97757
        clientCodexBg #2110A37F #2110A37F #2110A37F #2110A37F #2110A37F
        clientCodexFg #10A37F #10A37F #10A37F #10A37F #10A37F
        clientGeminiBg #213B82F6 #213B82F6 #213B82F6 #213B82F6 #213B82F6
        clientGeminiFg #3B82F6 #3B82F6 #3B82F6 #3B82F6 #3B82F6
        clientCursorBg #217C3AED #217C3AED #217C3AED #217C3AED #217C3AED
        clientCursorFg #7C3AED #7C3AED #7C3AED #7C3AED #7C3AED
        clientVSCodeBg #210284C7 #210284C7 #210284C7 #210284C7 #210284C7
        clientVSCodeFg #0284C7 #0284C7 #0284C7 #0284C7 #0284C7
        clientOpenCodeBg #2164748B #2164748B #2164748B #2164748B #2164748B
        clientOpenCodeFg #64748B #64748B #64748B #64748B #64748B
        clientQwenBg #216366F1 #216366F1 #216366F1 #216366F1 #216366F1
        clientQwenFg #6366F1 #6366F1 #6366F1 #6366F1 #6366F1
        clientKimiBg #210D9488 #210D9488 #210D9488 #210D9488 #210D9488
        clientKimiFg #0D9488 #0D9488 #0D9488 #0D9488 #0D9488
        clientYuanbaoBg #212563EB #212563EB #212563EB #212563EB #212563EB
        clientYuanbaoFg #2563EB #2563EB #2563EB #2563EB #2563EB
        clientNeutralBg #2164748B #2164748B #2164748B #2164748B #2164748B
        clientNeutralFg #64748B #64748B #64748B #64748B #64748B
        windowShadow #210F172A #A6000000 #210F172A #A6000000 Transparent
        floatOutline #170F172A #2E94A3B8 #170F172A #2E94A3B8 #FFFFFF
        floatShadow #290F172A #B8000000 #290F172A #B8000000 Transparent
        blkSuBg #1C0E7C86 #262DD4BF #1C0E7C86 #262DD4BF #000000
        blkSuFg #0E7C86 #5EEAD4 #0E7C86 #5EEAD4 #3FF23F
        blkHmBg #1ABE185D #29F472B6 #1ABE185D #29F472B6 #000000
        blkHmFg #BE185D #F9A8D4 #BE185D #F9A8D4 #1AEBFF
        """;

    private static readonly Dictionary<string, string[]> Rows = Values.Split('\n').Select(line => line.Trim().Split(' '))
        .ToDictionary(row => row[0], row => row[1..]);
    internal static IEnumerable<string> Keys => Rows.Keys;
    internal static Color Get(string theme, string key) =>
        (Color)ColorConverter.ConvertFromString(Rows[key][Array.IndexOf(Themes, theme)]);

    internal static void AssertContrast(string theme, string foreground, string background, string ground, double ratio, double minimum)
    {
        var bg = ThemeSurfaceTests.Composite(Get(theme, background), Get(theme, ground));
        var fg = ThemeSurfaceTests.Composite(Get(theme, foreground), bg);
        double a = ThemeSurfaceTests.Luminance(fg), b = ThemeSurfaceTests.Luminance(bg);
        double design = (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        Assert.Equal(design, ratio, 8);
        if (design >= minimum) Assert.True(ratio >= minimum, $"{theme} {foreground}/{background}: {ratio}");
    }
}
