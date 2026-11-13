import { render } from "solid-js/web";
import { App } from "./App";
import { applyTheme } from "./theme";
import { lang } from "./i18n";
import "./styles/app.css";

// Links and bookmarks from the first dashboard used #settings – the router uses #/settings
if (/^#[a-z]+$/.test(location.hash)) history.replaceState(null, "", "#/" + location.hash.slice(1));

applyTheme();
document.documentElement.lang = lang();
render(() => <App />, document.getElementById("root")!);
