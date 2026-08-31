namespace FoamWorkbench.Services;

public static class PorousUnitConverter
{
    public static double MillimetresToMetres(double value) => value * 1e-3;
    public static double MicrometresToMetres(double value) => value * 1e-6;
    public static double MillimetresPerHourToMetresPerSecond(double value) => value / 3_600_000.0;
    public static double MetresToMillimetres(double value) => value * 1e3;
    public static double MetresPerSecondToMillimetresPerHour(double value) => value * 3_600_000.0;
}
