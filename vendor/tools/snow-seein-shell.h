// GPL-3.0-only. Native settings actions return to SEEIN's Win11 settings surface.
#pragma once
#include <QLocalSocket>
#include <QCoreApplication>
namespace snow_shot {
inline void seeinOpenSettings() {
    auto* socket = new QLocalSocket(QCoreApplication::instance());
    QObject::connect(socket, &QLocalSocket::connected, socket, [socket] {
        socket->write("{\"id\":\"settings\",\"version\":1,\"operation\":\"settings\",\"action\":\"snow\"}\n");
        socket->flush(); socket->disconnectFromServer();
    });
    QObject::connect(socket, &QLocalSocket::disconnected, socket, &QObject::deleteLater);
    QObject::connect(socket, &QLocalSocket::errorOccurred, socket, [socket] { socket->deleteLater(); });
    socket->connectToServer(qEnvironmentVariable("SEEIN_SHELL_PIPE"));
}
}
