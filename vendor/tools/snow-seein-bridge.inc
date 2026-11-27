// SEEIN integration for the pinned Snow Shot source. GPL-3.0-only.
    std::unique_ptr<QLocalServer> seeinServer;
    static bool seeinMode() { return !qEnvironmentVariable("SEEIN_TOOL_PIPE").isEmpty(); }
    void openSeeinSettings() {
        auto* socket = new QLocalSocket(&q);
        QObject::connect(socket, &QLocalSocket::connected, socket, [socket] {
            socket->write("{\"id\":\"settings\",\"version\":1,\"operation\":\"settings\",\"action\":\"snow\"}\n");
            socket->flush(); socket->disconnectFromServer();
        });
        QObject::connect(socket, &QLocalSocket::disconnected, socket, &QObject::deleteLater);
        QObject::connect(socket, &QLocalSocket::errorOccurred, socket, [socket] { socket->deleteLater(); });
        socket->connectToServer(qEnvironmentVariable("SEEIN_SHELL_PIPE"));
    }
    static bool seeinSettingAllowed(const presentation::settings::SettingsFieldDescriptor& field) {
        const auto& key = field.configurationKey;
        if (key.isEmpty()) return false;
        const QStringList excluded{QStringLiteral("pin"), QStringLiteral("webcam"),
            QStringLiteral("keyboard"), QStringLiteral("mouse_click"), QStringLiteral("mouse_trail"),
            QStringLiteral("mouse_highlight"), QStringLiteral("record_mouse"), QStringLiteral("follow_mouse"),
            QStringLiteral("post_processing"), QStringLiteral("progress_bar"), QStringLiteral("qr_code"),
            QStringLiteral("cloud"), QStringLiteral("latex"), QStringLiteral("table_"),
            QStringLiteral("document_recognition"), QStringLiteral("conversion"), QStringLiteral("toolbar/")};
        for (const auto& word : excluded) if (key.contains(word)) return false;
        return key.startsWith(QStringLiteral("screenshot/")) ||
               key.startsWith(QStringLiteral("capture_history/")) ||
               key.startsWith(QStringLiteral("text_recognition/")) ||
               (key.startsWith(QStringLiteral("screenshot_translation/")) && !key.endsWith(QStringLiteral("/model"))) ||
               key.startsWith(QStringLiteral("screen_recording/")) ||
               key == QStringLiteral("interface/theme_mode") || key == QStringLiteral("interface/language");
    }
    CustomAiModelConfiguration seeinTranslationModel() {
        const auto models = runtimeSession->customAiModels();
        if (!models.isEmpty()) return models.first();
        QFile draft(QDir(qEnvironmentVariable("SEEIN_TOOL_DATA")).filePath(QStringLiteral("translation-draft.json")));
        CustomAiModelConfiguration model;
        if (draft.open(QIODevice::ReadOnly)) {
            const auto object = QJsonDocument::fromJson(draft.readAll()).object();
            model.baseUrl = object.value("baseUrl").toString(); model.model = object.value("model").toString();
            model.apiKey = seeinUnprotectSecret(object.value("apiKey").toString());
        }
        return model;
    }
    QJsonArray seeinFields(bool localKeys = false) {
        using Kind = presentation::settings::SettingsFieldKind;
        ensureSettingsRuntime(); QJsonArray fields;
        for (const auto& field : settingsRegistry->fields()) {
            if (localKeys ? field.kind != Kind::LocalShortcut ||
                !(field.configurationKey.startsWith(QStringLiteral("screenshot_shortcuts/")) ||
                  field.configurationKey.startsWith(QStringLiteral("screen_recording_shortcuts/")))
                : !seeinSettingAllowed(field)) continue;
            const auto state = runtimeSession->state(field.id);
            if (!state.visible || !state.enabled || !field.definition) continue;
            QString kind = QStringLiteral("string");
            if (field.kind == Kind::Switch) kind = QStringLiteral("bool");
            else if (field.kind == Kind::Integer || field.kind == Kind::Slider) kind = QStringLiteral("number");
            else if (field.kind == Kind::LocalShortcut) kind = QStringLiteral("shortcut");
            else if (field.kind != Kind::Select && field.kind != Kind::Radio && field.kind != Kind::Text &&
                     field.kind != Kind::FilePath && field.kind != Kind::DirectoryPath && field.kind != Kind::Color) continue;
            if (localKeys && (field.id.contains(QStringLiteral("pin")) || field.id.contains(QStringLiteral("trim")) ||
                field.id.contains(QStringLiteral("color-picker")) || field.id.contains(QStringLiteral("stitch")) ||
                field.id.contains(QStringLiteral("split")) || field.id.contains(QStringLiteral("angle")) ||
                field.id.contains(QStringLiteral("distance")))) continue;
            QJsonArray options;
            for (const auto& option : runtimeSession->options(field.id).values)
                options.append(QJsonObject{{"value", QJsonValue::fromVariant(option.value)}, {"label", option.label}});
            const auto* page = settingsRegistry->catalog().page(field.pageId);
            const auto jsonValue = localKeys ? QJsonValue(QJsonArray::fromStringList(shortcuts::portableTextList(state.acceptedValue.value<shortcuts::ShortcutBindingList>())))
                : field.kind == Kind::Color ? QJsonValue(state.acceptedValue.value<QColor>().name(QColor::HexArgb)) : QJsonValue::fromVariant(state.acceptedValue);
            QJsonObject item{{"id", field.id}, {"label", field.definition->title.translated()},
                {"section", page ? page->title.translated() : field.pageId}, {"kind", kind},
                {"value", jsonValue}, {"options", options},
                {"description", field.definition->description.translated()}};
            if (const auto* schema = storage::ConfigurationSchema::entry(field.configurationKey); schema && schema->integerRange) {
                item.insert("minimum", schema->integerRange->minimum);
                item.insert("maximum", schema->integerRange->maximum);
            }
            fields.append(item);
        }
        if (!localKeys) {
            const auto model = seeinTranslationModel();
            for (const auto& item : QVector<QPair<QString, QString>>{
                {QStringLiteral("baseUrl"), ApplicationController::tr("Translation API base URL")},
                {QStringLiteral("model"), ApplicationController::tr("Translation model")},
                {QStringLiteral("apiKey"), ApplicationController::tr("Translation API key")}}) {
                fields.append(QJsonObject{{"id", QStringLiteral("seein.translation.") + item.first},
                    {"label", item.second}, {"section", ApplicationController::tr("Screenshot translation")},
                    {"kind", item.first == QStringLiteral("apiKey") ? QStringLiteral("secret") : QStringLiteral("string")},
                    {"value", item.first == QStringLiteral("baseUrl") ? model.baseUrl : item.first == QStringLiteral("model") ? model.model : QString()}});
            }
        }
        return fields;
    }
    void seeinApply(const QJsonObject& values, bool shortcuts) {
        ensureSettingsRuntime();
        auto model = seeinTranslationModel();
        bool translated = false;
        for (auto it = values.begin(); it != values.end(); ++it) {
            if (it.key().startsWith(QStringLiteral("seein.translation.")) && !shortcuts) {
                const auto name = it.key().mid(18);
                if (name == QStringLiteral("baseUrl")) model.baseUrl = it.value().toString().trimmed();
                else if (name == QStringLiteral("model")) model.model = it.value().toString().trimmed();
                else if (name == QStringLiteral("apiKey")) model.apiKey = it.value().toString();
                else throw std::runtime_error("Unsupported translation setting");
                translated = true; continue;
            }
            const auto* field = settingsRegistry->field(it.key());
            if (!field || (shortcuts ? field->kind != presentation::settings::SettingsFieldKind::LocalShortcut : !seeinSettingAllowed(*field)))
                throw std::runtime_error("Unsupported setting");
            const auto value = field->kind == presentation::settings::SettingsFieldKind::Color ? QVariant::fromValue(QColor(it.value().toString())) : it.value().toVariant();
            if (!runtimeSession->submitDraft(it.key(), value))
                throw std::runtime_error(runtimeSession->state(it.key()).error.toStdString());
        }
        if (translated) {
            if (model.id.isEmpty()) model.id = QUuid::createUuid().toString(QUuid::WithoutBraces);
            model.name = QStringLiteral("SEEIN Translation"); model.provider = QStringLiteral("openai");
            // Incomplete setup can be saved one field at a time. It is not selected until complete.
            auto& config = storage::ApplicationStorage::instance().configuration();
            if (model.baseUrl.isEmpty() || model.model.isEmpty()) {
                // Preserve unfinished values outside the upstream validated model collection.
                QFile draft(QDir(qEnvironmentVariable("SEEIN_TOOL_DATA")).filePath(QStringLiteral("translation-draft.json")));
                if (draft.open(QIODevice::WriteOnly)) draft.write(QJsonDocument(QJsonObject{{"baseUrl", model.baseUrl}, {"model", model.model}, {"apiKey", seeinProtectSecret(model.apiKey)}}).toJson());
            } else {
                if (!runtimeSession->applyCustomAiModels({model})) throw std::runtime_error("Invalid translation API settings");
                config.setValue(QStringLiteral("screenshot_translation/model"), model.selectionId());
            }
        }
        static_cast<void>(storage::ApplicationStorage::instance().configuration().flushNow());
    }
    QJsonObject seeinRequest(const QJsonObject& request) {
        QJsonObject result{{"id", request.value("id")}, {"version", 1}, {"success", true}};
        try {
            if (request.value("version").toInt() != 1) throw std::runtime_error("Unsupported protocol version");
            const auto operation = request.value("operation").toString();
            if (operation == QStringLiteral("settings.get")) result.insert("fields", seeinFields());
            else if (operation == QStringLiteral("shortcuts.get")) result.insert("fields", seeinFields(true));
            else if (operation == QStringLiteral("settings.apply") || operation == QStringLiteral("shortcuts.apply"))
                seeinApply(request.value("values").toObject(), operation == QStringLiteral("shortcuts.apply"));
            else if (operation == QStringLiteral("shutdown")) {
                if (screenshotController && screenshotController->blocksApplicationUpdate(false))
                    throw std::runtime_error("Finish recording or export before exiting");
                QTimer::singleShot(150, &q, [this] { app.quit(); });
            } else if (operation == QStringLiteral("execute")) {
                using Quick = presentation::GlobalShortcutAction;
                const auto action = request.value("action").toString();
                const QHash<QString, Quick> actions{{"snow.capture", Quick::Screenshot}, {"snow.copy", Quick::ScreenshotCopy},
                    {"snow.window", Quick::ScreenshotFocusedWindow}, {"snow.screen", Quick::ScreenshotFullScreen},
                    {"snow.delay", Quick::ScreenshotDelay}, {"snow.scrolling", Quick::ScreenshotScrolling},
                    {"snow.record", Quick::ScreenRecord}, {"snow.ocr", Quick::ScreenshotOcr},
                    {"snow.translate", Quick::ScreenshotTranslation}, {"snow.history", Quick::OpenCaptureHistory}};
                if (action == QStringLiteral("snow.pdf")) ensureImageStitchingWindowCoordinator().present();
                else if (actions.contains(action)) {
                    if (action == QStringLiteral("snow.translate") && runtimeSession->customAiModels().isEmpty())
                        throw std::runtime_error("Configure a translation API in SEEIN first. Offline OCR remains available.");
                    dispatchQuickAction(actions.value(action));
                } else throw std::runtime_error("Unsupported action");
            } else throw std::runtime_error("Unsupported operation");
        } catch (const std::exception& error) { result.insert("success", false); result.insert("error", QString::fromUtf8(error.what())); }
        return result;
    }
    void startSeeinBridge() {
        if (!seeinMode()) return;
        ensureSettingsRuntime();
        globalShortcutManager.setGlobalShortcutsEnabled(false);
        globalMouseManager.setGlobalMouseEnabled(false);
        seeinServer = std::make_unique<QLocalServer>();
        seeinServer->setSocketOptions(QLocalServer::UserAccessOption);
        QObject::connect(seeinServer.get(), &QLocalServer::newConnection, &q, [this] {
            while (auto* socket = seeinServer->nextPendingConnection()) {
                QObject::connect(socket, &QLocalSocket::disconnected, socket, &QObject::deleteLater);
                QObject::connect(socket, &QLocalSocket::readyRead, socket, [this, socket] {
                    if (socket->bytesAvailable() > 1024 * 1024) { socket->abort(); return; }
                    if (!socket->canReadLine()) return;
                    const auto request = QJsonDocument::fromJson(socket->readLine()).object();
                    const auto response = seeinRequest(request);
                    socket->write(QJsonDocument(response).toJson(QJsonDocument::Compact) + '\n');
                    socket->flush(); socket->disconnectFromServer();
                });
            }
        });
        if (!seeinServer->listen(qEnvironmentVariable("SEEIN_TOOL_PIPE")))
            QTimer::singleShot(0, &q, [this] { app.quit(); });
    }
