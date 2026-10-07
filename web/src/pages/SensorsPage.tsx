import { useApp } from "../context/AppContext";
import { HomeAssistantSetupHelp, SensorsSection } from "../components/EnergyEvidence";
import { StandingChargeSetting } from "../components/setup/StandingChargeSetting";

/** Setup › Sensors: the Home Assistant sensors Joule reads, their latest readings and how to map them. */
export default function SensorsPage() {
  const { measured } = useApp();
  return (
    <div className="sensors-page">
      <p className="muted">Readings are collected every 5 minutes, whether or not this page is open.</p>
      {/* The strip has its own "Read now"; every sensor is listed, so there is no Show all / Hide. */}
      <SensorsSection status={measured.telemetry} />
      <StandingChargeSetting />
      <HomeAssistantSetupHelp summary="Set up or change sensors (for installers)" />
    </div>
  );
}
