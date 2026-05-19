using System;

namespace AppleMusicTranslator;

[Flags]
internal enum SettingsChangeKind
{
    None = 0,
    General = 1,
    Layout = 2,
    Translation = 4,
    Language = 8
}
