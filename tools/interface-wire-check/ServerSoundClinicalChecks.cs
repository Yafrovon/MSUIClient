using MSUIClient;
using MSUIClient.Net;

internal static class ServerSoundClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.SMSG_PLAY_MUSIC == 0x0277 &&
              (ushort)Op.SMSG_PLAY_OBJECT_SOUND == 0x0278 &&
              (ushort)Op.SMSG_PLAY_SOUND == 0x02D2,
            "server sound opcode identity drift");

        ServerSoundPacket sound = ServerSoundPackets.ParseSound(
            Convert.FromHexString("44332211"));
        ServerSoundPacket music = ServerSoundPackets.ParseMusic(
            Convert.FromHexString("88776655"));
        ServerObjectSoundPacket obj = ServerSoundPackets.ParseObjectSound(
            Convert.FromHexString("040302011817161514131211"));
        Check(sound.SoundId == 0x11223344 && music.SoundId == 0x55667788 &&
              obj.SoundId == 0x01020304 && obj.SourceGuid == 0x1112131415161718,
            "server sound little-endian packet shape drift");

        ExpectInvalid(() => ServerSoundPackets.ParseSound(new byte[3]));
        ExpectInvalid(() => ServerSoundPackets.ParseMusic(new byte[5]));
        ExpectInvalid(() => ServerSoundPackets.ParseObjectSound(new byte[11]));
        ExpectInvalid(() => ServerSoundPackets.ParseObjectSound(new byte[13]));

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string glue = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        string soundscape = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "WorldSoundscape.cs"));

        Check(dispatch.Contains("case Op.SMSG_PLAY_SOUND:", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_PLAY_MUSIC:", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_PLAY_OBJECT_SOUND:", StringComparison.Ordinal) &&
              dispatch.Contains("ApplyServerSound(packetOpcode, body);", StringComparison.Ordinal),
            "server sound network dispatch drift");
        Check(glue.Contains("ServerSoundPacket sound = ServerSoundPackets.ParseSound(body);",
                  StringComparison.Ordinal) &&
              glue.Contains("ServerSoundPacket music = ServerSoundPackets.ParseMusic(body);",
                  StringComparison.Ordinal) &&
              glue.Contains("ServerObjectSoundPacket sound = ServerSoundPackets.ParseObjectSound(body);",
                  StringComparison.Ordinal) &&
              glue.Contains("=> !_soundscapePlaybackArmed || _worldLoading || _soundscape is null;",
                  StringComparison.Ordinal) &&
              glue.Contains("pose.Found ? pose.Position : listener", StringComparison.Ordinal) &&
              glue.Contains("trackHold: false, category: \"sfx\"", StringComparison.Ordinal),
            "world-hold drop or object-sound 3D/flat fallback drift");
        Check(soundscape.Contains("_musicKit == kit && _musicVoice != 0 && _mixer.IsLive(_musicVoice)",
                  StringComparison.Ordinal) &&
              soundscape.Contains("_musicFadeStartedAt = now;", StringComparison.Ordinal) &&
              soundscape.Contains("StartMusicKit(kit, \"server push\", now);", StringComparison.Ordinal),
            "server music repeat guard or shared-slot transition drift");
    }

    private static void ExpectInvalid(Action action)
    {
        try
        {
            action();
            throw new InvalidDataException("malformed server sound body was accepted");
        }
        catch (InvalidDataException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
