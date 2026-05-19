# Nexthire API

Backend API desarrollado en .NET 8 con patrón MVC (Model-View-Controller).

## Estructura del Proyecto

```
nexthire-api/
├── Controllers/          # Controladores MVC (Endpoints REST)
│   ├── UsersController.cs
│   └── JobsController.cs
├── Models/              # Modelos de datos
│   ├── User.cs
│   └── Job.cs
├── DTOs/                # Data Transfer Objects
│   ├── UserDto.cs
│   └── JobDto.cs
├── Services/            # Lógica de negocio
│   ├── IUserService.cs
│   ├── UserService.cs
│   ├── IJobService.cs
│   └── JobService.cs
├── Data/                # Contexto de base de datos
│   └── ApplicationDbContext.cs
├── Program.cs           # Configuración de la aplicación
└── appsettings.json     # Configuración
```

## Tecnologías Utilizadas

- .NET 8.0
- ASP.NET Core Web API
- Entity Framework Core (In-Memory Database)
- JWT Authentication (Bearer Tokens)
- Swagger/OpenAPI
- CORS habilitado

## Requisitos

- .NET 8.0 SDK o superior

## Instalación y Ejecución

1. Restaurar dependencias:
```bash
dotnet restore
```

2. Ejecutar la aplicación:
```bash
dotnet run
```

3. Acceder a Swagger UI:
```
https://localhost:5001/swagger
```

## Endpoints Disponibles

### Authentication API (Magic Link con Nexa)

- `POST /api/auth/magic-link` - Solicitar magic link (público)
  ```json
  {
    "email": "user@example.com"
  }
  ```

- `POST /api/auth/exchange` - Intercambiar token de Nexa por JWT de NextHire (público)
  ```json
  {
    "token": "nexa-token-from-email"
  }
  ```
  Respuesta:
  ```json
  {
    "accessToken": "jwt-token",
    "user": { "id": "...", "email": "...", "name": "..." },
    "org": { "id": "...", "name": "..." },
    "subscription": { "planKey": "...", "status": "..." }
  }
  ```

- `GET /api/auth/me` - Obtener información del usuario autenticado (requiere autenticación)
  Headers: `Authorization: Bearer <jwt-token>`

- `POST /api/auth/logout` - Cerrar sesión (requiere autenticación, stateless)
  Headers: `Authorization: Bearer <jwt-token>`

### Users API
- `GET /api/users` - Obtener todos los usuarios
- `GET /api/users/{id}` - Obtener usuario por ID
- `POST /api/users` - Crear nuevo usuario
- `PUT /api/users/{id}` - Actualizar usuario
- `DELETE /api/users/{id}` - Eliminar usuario

### Jobs API
- `GET /api/jobs` - Obtener todos los trabajos
- `GET /api/jobs/{id}` - Obtener trabajo por ID
- `POST /api/jobs` - Crear nuevo trabajo
- `PUT /api/jobs/{id}` - Actualizar trabajo
- `DELETE /api/jobs/{id}` - Eliminar trabajo

## Ejemplo de Uso

### Crear un usuario:
```json
POST /api/users
{
  "firstName": "Juan",
  "lastName": "Pérez",
  "email": "juan.perez@example.com",
  "phone": "+1234567890"
}
```

### Crear un trabajo:
```json
POST /api/jobs
{
  "title": "Desarrollador Full Stack",
  "description": "Buscamos desarrollador con experiencia en .NET y React",
  "company": "Tech Corp",
  "location": "Ciudad de México",
  "salary": 50000
}
```

## Configuración de Autenticación

La autenticación utiliza Magic Link con Nexa como Identity Provider. Configura los siguientes valores en `appsettings.json`:

```json
{
  "Jwt": {
    "Issuer": "nexthire-api",
    "Audience": "nexthire-api",
    "SigningKey": "YourSecretSigningKeyForJWTTokenGeneration-MustBeAtLeast32Characters",
    "ExpiresMinutes": "60"
  },
  "Nexa": {
    "BaseUrl": "https://api.nexa.com",
    "ApiKey": "your-nexa-api-key-here"
  }
}
```

**Importante:** 
- Cambia `Jwt:SigningKey` por una clave secreta segura en producción
- Configura `Nexa:BaseUrl` y `Nexa:ApiKey` con los valores reales de Nexa
- El frontend debe redirigir a `https://{NEXT_HIRE_WEB_URL}/auth/callback?token=...` después de recibir el magic link

## Seguridad

- Todos los endpoints requieren autenticación por defecto (FallbackPolicy)
- Los endpoints `/api/auth/magic-link` y `/api/auth/exchange` están marcados como `[AllowAnonymous]`
- La autenticación es stateless (solo JWT, sin refresh tokens)
- No se almacenan usuarios en base de datos (los datos vienen de Nexa)

## Notas

- La base de datos es In-Memory, por lo que los datos se pierden al reiniciar la aplicación
- Para producción, se recomienda cambiar a una base de datos persistente (SQL Server, PostgreSQL, etc.)
- CORS está configurado para permitir todas las solicitudes (ajustar según necesidades de seguridad)
- La autenticación no requiere base de datos - es completamente stateless

