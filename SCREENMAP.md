# Mapa visual (a partir de `apktool d base.apk --no-src`, só estrutura)

Fonte: `~/carris-app/visual/app/res/`. Textos PT vêm dos resources .NET
(`PtStrManager`), não de `values-pt` (não existe; `strings.xml` traz só
framework + `ApplicationName=Carris`).

## Ecrãs (layout -> activity observada)
| Ecrã rebuild | Layout(s) original | Estrutura observada |
|---|---|---|
| Splash | `splash_activity.xml` | LinearLayout bottom: versão + "powered by" + logo 80dip |
| Main (tabs) | `main_activity.xml` + `CarrisSectionsPagerAdapter` | TabLayout (indicador `colorAccent` 3dip) + Coordinator + FAB `my_location` |
| Main/Home | `main_activity_mf.xml` | NestedScrollView, cards 10dip radius: `btn_go`, `location_input_list_card`, favoritos (`mf_fav_location_item`, `mf_fav_offline_voyage_item`), estimativas seguidas (`mf_tracked_estimation`) |
| Paragens | `main_activity_bsf.xml` + `bsf_stop_card_item` | Cards de paragem |
| Carreiras | `main_activity_rf.xml` + `rf_route_item` + `route_badge` | Lista com crachá de linha |
| Sobre | `main_activity_af.xml` + `af_item`, `af_about_info`, `af_temp` | Info + about |
| Alertas | `alerts_activity.xml` + `alert_list_item`, `alert_banner` | Lista + banner |
| Pesquisa paragem | `bus_stop_search_activity.xml` + `stop_list_item`, `search_empty_view` | Search + lista + empty view |
| Detalhe paragem | `bus_stop_details_activity.xml` + `estimation_item`, `no_estimations_item` | Toolbar + Coordinator + AppBar colapsável com mapa (`map_height`) + lista estimativas |
| Pesquisa carreira | `route_search_activity.xml` | Search + lista |
| Detalhe carreira | `route_details_activity.xml` + `route_activity_bs_item` | Paragens da carreira |
| Horários | `stop_times_activity.xml` + `schedule_hour_list_item`, `schedule_picker_item` | Seletor data + horas |
| Planeador | `planner_activity.xml` + `tf_dialog`, `solution_list_item` | Origem/destino + soluções |
| Passos | `solution_steps_activity.xml` + `solution_step_item`, `solution_step_item_bus` | Etapas (bus destaca-se) |
| Mapa popup | `map_popup_activity.xml` + `map_balloon` | Balão no mapa |
| Picker | `location_picker_activity.xml` + `location_picker_item` | Escolha de local |
| Diálogos | `pick_a_route_dialog`, `pick_a_variant_dialog` (+`rv_item`), `pick_a_navigation_route_dialog`, `main_start_terms_dialog`, `custom_dialog`, `dialog` | Seleção rota/variante, termos iniciais |
| Web | `webview_activity.xml` | m.carris.pt (tarifário, rede vendas, notícias) |
| Banners | `offline_banner.xml`, `expand_button`, `card_header`, `image_frame` | Offline + cartões |

## Marca/cores (não copiar assets nem chaves)
- Amarelo Carris `accent #ffd214`, fundo `#eeeeee`, header próprio; TabLayout com indicador accent.
- Assets `logo_carris.png`, `carris.png`, `app_icon.png`, `bus_with/without.png`, `svg_*pin*.xml`, `route_badge*` são marca da Carris — o rebuild usa arte própria.
- Segredos encontrados e **a não reutilizar**: `AppCenterSecret=1adeb567-…`, Google Maps `AIzaSyCq…` (no manifest). O rebuild usa chaves próprias.
