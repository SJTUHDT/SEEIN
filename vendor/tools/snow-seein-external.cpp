// GPL-3.0-only. SEEIN launches browsers/documents outside the native tool job.
#include "snow_shot/seeinexternal.h"
#include <QCoreApplication>
#include <QDesktopServices>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLocalSocket>

namespace snow_shot {
bool seeinOpenUrl(const QUrl& url) {
    if (qEnvironmentVariable("SEEIN_TOOL_PIPE").isEmpty()) return QDesktopServices::openUrl(url);
    auto* socket = new QLocalSocket(QCoreApplication::instance());
    QObject::connect(socket, &QLocalSocket::connected, socket, [socket, url] {
        const QJsonObject request{
            {QStringLiteral("id"), QStringLiteral("open-external")}, {QStringLiteral("version"), 1},
            {QStringLiteral("operation"), QStringLiteral("external")}, {QStringLiteral("action"), QStringLiteral("snow")},
            {QStringLiteral("values"), QJsonObject{{QStringLiteral("url"), url.toString()}}}};
        socket->write(QJsonDocument(request).toJson(QJsonDocument::Compact) + '\n');
        socket->flush(); socket->disconnectFromServer();
    });
    QObject::connect(socket, &QLocalSocket::disconnected, socket, &QObject::deleteLater);
    QObject::connect(socket, &QLocalSocket::errorOccurred, socket, [socket] { socket->deleteLater(); });
    socket->connectToServer(qEnvironmentVariable("SEEIN_SHELL_PIPE"));
    return true;
}
}
