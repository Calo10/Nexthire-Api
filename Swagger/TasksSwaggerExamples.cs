using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using nexthire_api.Controllers;
using nexthire_api.Models.Tasks;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace nexthire_api.Swagger;

/// <summary>
/// Adds explicit examples for /api/tasks so Swagger "Example Value"
/// matches the API response/request shapes (field names, nullability, status values).
/// </summary>
public sealed class TasksOperationExamples : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var isTasksController = context.MethodInfo.DeclaringType == typeof(TasksController);
        if (!isTasksController)
            return;

        // Response examples
        if (operation.Responses.TryGetValue("200", out var okResponse))
        {
            if (okResponse.Content.TryGetValue("application/json", out var okContent))
            {
                // GET /api/tasks returns array
                if (context.MethodInfo.Name == nameof(TasksController.GetTasks))
                {
                    okContent.Example = new OpenApiArray
                    {
                        TasksExamples.TaskDtoExample()
                    };
                }
                // GET /api/tasks/{id} + PATCH /status returns object
                else
                {
                    okContent.Example = TasksExamples.TaskDtoExample();
                }
            }
        }

        if (operation.Responses.TryGetValue("201", out var createdResponse))
        {
            if (createdResponse.Content.TryGetValue("application/json", out var createdContent))
            {
                createdContent.Example = TasksExamples.TaskDtoExample();
            }
        }

        // Request examples
        if (operation.RequestBody != null && operation.RequestBody.Content.TryGetValue("application/json", out var reqContent))
        {
            if (context.MethodInfo.Name == nameof(TasksController.CreateTask))
                reqContent.Example = TasksExamples.CreateTaskRequestExample();

            if (context.MethodInfo.Name == nameof(TasksController.UpdateTaskStatus))
                reqContent.Example = TasksExamples.UpdateTaskStatusRequestExample();
        }
    }
}

public sealed class TasksSchemaExamples : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(TaskDto))
            schema.Example = TasksExamples.TaskDtoExample();

        if (context.Type == typeof(CreateTaskRequest))
            schema.Example = TasksExamples.CreateTaskRequestExample();

        if (context.Type == typeof(UpdateTaskStatusRequest))
            schema.Example = TasksExamples.UpdateTaskStatusRequestExample();
    }
}

internal static class TasksExamples
{
    internal static OpenApiObject TaskDtoExample()
    {
        return new OpenApiObject
        {
            ["id"] = new OpenApiString("11111111-1111-1111-1111-111111111111"),
            ["title"] = new OpenApiString("Follow up with candidate"),
            ["status"] = new OpenApiString("todo"),
            ["dueAt"] = new OpenApiString("2026-01-21T15:00:00Z"),
            ["applicationId"] = new OpenApiString("22222222-2222-2222-2222-222222222222"),
            ["jobId"] = new OpenApiString("33333333-3333-3333-3333-333333333333"),
            ["candidateId"] = new OpenApiString("44444444-4444-4444-4444-444444444444"),
            ["assignedToUserId"] = new OpenApiString("55555555-5555-5555-5555-555555555555"),
            ["jobTitle"] = new OpenApiString("Senior Backend Engineer"),
            ["candidateName"] = new OpenApiString("Carlos Mendez"),
            ["assignedToName"] = new OpenApiString("Recruiter 1"),
            ["createdAt"] = new OpenApiString("2026-01-20T12:00:00Z"),
            // Not completed yet (null unless status == done)
            ["completedAt"] = new OpenApiNull()
        };
    }

    internal static OpenApiObject CreateTaskRequestExample()
    {
        return new OpenApiObject
        {
            ["title"] = new OpenApiString("Follow up with candidate"),
            ["status"] = new OpenApiString("todo"),
            ["dueAt"] = new OpenApiString("2026-01-21T15:00:00Z"),
            ["applicationId"] = new OpenApiString("22222222-2222-2222-2222-222222222222"),
            ["assignedToUserId"] = new OpenApiString("55555555-5555-5555-5555-555555555555")
        };
    }

    internal static OpenApiObject UpdateTaskStatusRequestExample()
    {
        return new OpenApiObject
        {
            ["status"] = new OpenApiString("in_progress")
        };
    }
}

