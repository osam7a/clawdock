namespace Clawdock.Tests
{
    static class ShortcutTests
    {
        const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;
        const int F9 = 0x78, F24 = 0x87, K = 0x4B, Space = 0x20;

        public static void A_shortcut_needs_a_modifier_or_a_function_key()
        {
            Check.That(Hotkeys.CanUse(Ctrl | Shift, Space), "Ctrl + Shift + Space");
            Check.That(Hotkeys.CanUse(Win, K), "Win + K");
            Check.That(Hotkeys.CanUse(0, F9), "F9 by itself");
            Check.That(Hotkeys.CanUse(0, F24), "F24 by itself");
            Check.That(!Hotkeys.CanUse(0, K), "K by itself");
            Check.That(!Hotkeys.CanUse(0, Space), "Space by itself");
        }

        public static void The_modifier_keys_only_count_as_modifiers()
        {
            foreach (int vk in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            {
                Check.That(Hotkeys.IsModifier(vk), "key " + vk);
                Check.That(!Hotkeys.CanUse(Ctrl, vk), "Ctrl plus modifier key " + vk);
            }
            Check.That(!Hotkeys.IsModifier(K), "K");
        }

        public static void Held_keys_read_in_the_same_order_as_a_full_shortcut()
        {
            Check.Equal("", Hotkeys.DescribeModifiers(0));
            Check.Equal("Ctrl + ", Hotkeys.DescribeModifiers(Ctrl));
            Check.Equal("Ctrl + Alt + Shift + Win + ", Hotkeys.DescribeModifiers(Alt | Ctrl | Shift | Win));
            Check.Equal("Ctrl + Shift + K", Hotkeys.Describe(Ctrl | Shift, K));
            Check.Equal("F9", Hotkeys.Describe(0, F9));
        }
    }
}
