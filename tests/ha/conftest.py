"""HA tests for custom_components/tesla_bridge (pytest-homeassistant-custom-component)."""

from pathlib import Path

import pytest

pytest_plugins = "pytest_homeassistant_custom_component"

REPO_COMPONENTS = str(Path(__file__).resolve().parents[2] / "custom_components")


@pytest.fixture(autouse=True)
def auto_enable_custom_integrations(enable_custom_integrations):
    # The plugin's testing_config has its own custom_components package; add our folder to it.
    import custom_components

    if REPO_COMPONENTS not in custom_components.__path__:
        custom_components.__path__.append(REPO_COMPONENTS)
    yield
