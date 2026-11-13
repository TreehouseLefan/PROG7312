namespace SmartX.Core.Mesh;

/// <summary>
/// First-order radio energy model (Heinzelman, Chandrakasan and Balakrishnan, 2000) used to weight each
/// wireless link by the energy needed to send one packet across it:
/// <code>
/// E_tx(k, d) = E_elec·k + ε_amp·k·d²     E_rx(k) = E_elec·k     link cost = E_tx + E_rx
/// </code>
/// with E_elec = 50 nJ/bit and ε_amp = 100 pJ/bit/m². Because the amplifier term grows with distance squared,
/// two short hops can cost less energy than one long hop, which is why a mesh saves battery.
/// </summary>
public sealed record RadioEnergyModel(
    double ElectronicsJoulesPerBit = 50e-9,
    double AmplifierJoulesPerBitPerSquareMetre = 100e-12,
    int PacketBits = 1024,
    double RangeMetres = 40)
{
    public static RadioEnergyModel Default { get; } = new();

    /// <summary>Wired, mains-powered gateway-to-server backhaul costs no battery energy.</summary>
    public double BackhaulCostMicrojoules => 0;

    public double TransmitMicrojoules(double distanceMetres) =>
        PacketBits * (ElectronicsJoulesPerBit + (AmplifierJoulesPerBitPerSquareMetre * distanceMetres * distanceMetres)) * 1e6;

    public double ReceiveMicrojoules => PacketBits * ElectronicsJoulesPerBit * 1e6;

    public double LinkCostMicrojoules(double distanceMetres) =>
        TransmitMicrojoules(distanceMetres) + ReceiveMicrojoules;
}