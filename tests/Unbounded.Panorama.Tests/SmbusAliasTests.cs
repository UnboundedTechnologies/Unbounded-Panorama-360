using Unbounded.Panorama.Lighting;
using Xunit;

namespace Unbounded.Panorama.Tests;

/// <summary>The same RAM stick listed twice through two ports of one SMBus controller is controlled once.</summary>
public class SmbusAliasTests
{
    private static RgbHub.SmbusDevice Ram(int index, int port, string address, string version = "AUDA0-E6K5-0101", int leds = 8) =>
        new(index, "ENE DRAM", $"I2C: PawnIO SMBus PIIX4 {port}, address {address}", version, leds);

    [Fact]
    public void Two_sticks_listed_on_two_ports_keep_only_the_first_port()
    {
        // What OpenRGB 1.0 listed on an X870 board with two sticks (a colour sent to port 1 lit the real sticks).
        var aliases = RgbHub.SmbusAliases([Ram(0, 0, "0x71"), Ram(1, 0, "0x73"), Ram(2, 1, "0x71"), Ram(3, 1, "0x73")]);
        Assert.Equal(new HashSet<int> { 2, 3 }, aliases);
    }

    [Fact]
    public void Different_addresses_on_another_port_are_different_sticks()
    {
        Assert.Empty(RgbHub.SmbusAliases([Ram(0, 0, "0x71"), Ram(1, 1, "0x72")]));
    }

    [Fact]
    public void Different_firmware_or_led_count_is_a_different_device()
    {
        Assert.Empty(RgbHub.SmbusAliases([Ram(0, 0, "0x71"), Ram(1, 1, "0x71", version: "OTHER")]));
        Assert.Empty(RgbHub.SmbusAliases([Ram(0, 0, "0x71"), Ram(1, 1, "0x71", leds: 10)]));
    }

    [Fact]
    public void Two_sticks_on_one_bus_and_usb_devices_are_never_aliases()
    {
        // Corsair RAM on an Intel SMBus (one port), a GPU, and USB devices: nothing is left out.
        Assert.Empty(RgbHub.SmbusAliases(
        [
            new(0, "Corsair Vengeance RGB Pro DDR4", "I2C: /dev/i2c-0, address 0x58", "", 10),
            new(1, "Corsair Vengeance RGB Pro DDR4", "I2C: /dev/i2c-0, address 0x59", "", 10),
            new(2, "ASUS ROG ASTRAL GeForce RTX 5080 OC WHITE", "I2C: Nvidia NvAPI I2C on GPU 0, address 0x67", "", 30),
            new(3, "Wooting Two HE (ARM) zone", @"HID: \\?\HID#VID_31E3&PID_1231", "", 110),
        ]));
    }

    [Fact]
    public void A_listing_where_the_second_port_comes_first_still_keeps_the_lowest_port()
    {
        Assert.Equal(new HashSet<int> { 0 }, RgbHub.SmbusAliases([Ram(0, 1, "0x71"), Ram(1, 0, "0x71")]));
    }
}
